using Microsoft.Data.SqlClient;
using Telegram.Bot;
using Telegram.Bot.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using TelegramBotApp.Converter;
using TelegramBotApp.Savers;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- Configurar servicios ---
// lee variables desde appsettings o variables de entorno (Azure App Settings)
var telegramToken = config["TelegramToken"];
var geminiApiKey = config["GeminiApiKey"];
var connectionString = config.GetConnectionString("DefaultConnection") ?? config["ConnectionStrings:DefaultConnection"];

// valida que existan (en desarrollo puedes lanzar excepción si faltan)
if (string.IsNullOrEmpty(telegramToken))
    throw new InvalidOperationException("Falta TELEGRAM token en configuración (TelegramToken).");
if (string.IsNullOrEmpty(connectionString))
    throw new InvalidOperationException("Falta cadena de conexión en configuración (ConnectionStrings:DefaultConnection).");

// Registrar cliente Telegram
builder.Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(telegramToken));

// Registrar GeminiService como typed client (inyecta HttpClient y la apiKey)
builder.Services.AddHttpClient<GeminiService>()
    .ConfigureHttpClient(client => {
        // opcional: timeout o headers globales
        client.Timeout = TimeSpan.FromSeconds(60);
    });
builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<HttpClient>();
    return new GeminiService(geminiApiKey, http);
});

var app = builder.Build();

// --- Detectar si se está en Azure o local ---
var appBaseUrl = config["AppBaseUrl"]; // si está configurada en Azure
var botClient = app.Services.GetRequiredService<ITelegramBotClient>();

if (!string.IsNullOrEmpty(appBaseUrl))
{
    Console.WriteLine("Ejecutando en modo azurel.");
    // 🌐 Modo producción (Azure): usar Webhook
    var webhookUrl = $"{appBaseUrl.TrimEnd('/')}/bot/update";
    Console.WriteLine($"Setting Telegram webhook -> {webhookUrl}");
    await botClient.SetWebhookAsync(webhookUrl);
    Console.WriteLine("Webhook registrado.");
}
else
{
    // 💻 Modo local: usar Long Polling
    Console.WriteLine("Ejecutando en modo local (long polling).");

    var cts = new CancellationTokenSource();

    // arrancar polling en background
    _ = Task.Run(async () =>
    {
        var offset = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                await botClient.DeleteWebhookAsync();
                var updates = await botClient.GetUpdatesAsync(offset, cancellationToken: cts.Token);
                foreach (var update in updates)
                {
                    offset = update.Id + 1;

                    Console.WriteLine($"El update recibido fue: {update}");
                    if (update.Message?.Text == null) continue;

                    var messageText = update.Message.Text;
                    var chatId = update.Message.Chat.Id;
                    var user = update.Message.Chat.Username
                               ?? update.Message.Chat.FirstName
                               ?? update.Message.Chat.Id.ToString();

                    await GuardarMensajeAsync(connectionString, user, messageText);
                    var contexto = await ObtenerContextoAsync(connectionString, user, limite: 50);
                    var respuesta = await app.Services.GetRequiredService<GeminiService>()
                        .ConsultarGemini(contexto, messageText);

                    await botClient.SendTextMessageAsync(chatId, respuesta, cancellationToken: cts.Token);
                    Console.WriteLine($"📩 Update recibido: {JsonSerializer.Serialize(update)}");
                    Console.WriteLine($"📩 Update recibido local: {messageText}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error en long polling: {ex.Message}");
                await Task.Delay(2000, cts.Token); // espera 2s antes de reintentar
            }
        }
    });
}

// Endpoint Webhook (solo Azure lo usa)

app.MapPost("/bot/update", async (
    JsonElement body,
    ITelegramBotClient botClient,
    GeminiService geminiService,
    CancellationToken cancellationToken) =>
{
    Console.WriteLine("🚀 Iniciando procesamiento de update...");
    Console.WriteLine($"📥 Body recibido (raw JSON): {body.GetRawText()}");

    try
    {
        // Configurar opciones de deserialización con el conversor UnixTime → DateTime
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter()); // Para enums
        options.Converters.Add(new UnixDateTimeConverter());   // Para el campo date

        // Deserializar update con el converter
        var update = JsonSerializer.Deserialize<Update>(body.GetRawText(), options);
        Console.WriteLine($"📝 Update deserializado: {update != null}");

        if (update?.Message?.Text == null)
        {
            Console.WriteLine("⚠️ El update no contiene texto, se responde 200 OK sin procesar.");
            return Results.Ok(update);
        }

        var messageText = update.Message.Text;
        var chatId = update.Message.Chat.Id;
        var username = update.Message.Chat.Username ?? chatId.ToString();
        var firstName = update.Message.Chat.FirstName ?? string.Empty;
        var lastName = update.Message.Chat.LastName ?? string.Empty;

        Console.WriteLine($"👤 Usuario: {username}, Nombre: {firstName} {lastName}, Mensaje: {messageText}");

        // Guarda o actualiza usuario
        await UserSaverAsync.GuardarUsuarioAsync(connectionString, chatId, firstName, lastName);

        // Guarda mensaje
        await GuardarMensajeAsync(connectionString, username, messageText);

        // Obtiene contexto y consulta Gemini
        var contexto = await ObtenerContextoAsync(connectionString, username, limite: 50);
        var respuesta = await geminiService.ConsultarGemini(contexto, messageText);

        // Responde en Telegram
        await botClient.SendTextMessageAsync(chatId, respuesta, cancellationToken: cancellationToken);
        Console.WriteLine($"📩 Respuesta enviada a {chatId}: {respuesta}");

        return Results.Ok();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error procesando update: {ex}");
        return Results.Ok();
    }
});

// Endpoint para guardar texto OCR (agrega esto antes de app.Run())
app.MapPost("/api/mensajes/guardar-ocr", async (
    JsonElement body,
    GeminiService geminiService,
    CancellationToken cancellationToken) =>
{
    Console.WriteLine("🚀 Iniciando procesamiento de update...");
    Console.WriteLine($"📥 Body recibido (raw JSON): {body.GetRawText()}");

    try
    {
        // Configurar opciones de deserialización con el conversor UnixTime → DateTime
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter()); // Para enums
        options.Converters.Add(new UnixDateTimeConverter());   // Para el campo date

        // Deserializar update con el converter
        var update = JsonSerializer.Deserialize<Update>(body.GetRawText(), options);
        Console.WriteLine($"📝 Update deserializado: {update != null}");

        if (update?.Message?.Text == null)
        {
            Console.WriteLine("⚠️ El update no contiene texto, se responde 200 OK sin procesar.");
            return Results.Ok(update);
        }

        var messageText = update.Message.Text;
        var chatId = update.Message.Chat.Id;
        var username = update.Message.Chat.Username ?? chatId.ToString();
        var firstName = update.Message.Chat.FirstName ?? string.Empty;
        var lastName = update.Message.Chat.LastName ?? string.Empty;

        Console.WriteLine($"👤 Usuario: {username}, Nombre: {firstName} {lastName}, Mensaje: {messageText}");

        // Guarda o actualiza usuario
        await UserSaverAsync.GuardarUsuarioAsync(connectionString, chatId, firstName, lastName);

        // Guarda mensaje
        await GuardarMensajeAsync(connectionString, username, messageText);

        // Obtiene contexto y consulta Gemini
        var contexto = await ObtenerContextoAsync(connectionString, username, limite: 50);
        var respuesta = await geminiService.ConsultarGemini(contexto, messageText);

        // Responde en Telegram
        await botClient.SendTextMessageAsync(chatId, respuesta, cancellationToken: cancellationToken);
        Console.WriteLine($"📩 Respuesta enviada a {chatId}: {respuesta}");

        return Results.Ok();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Error procesando update: {ex}");
        return Results.Ok();
    }
});

app.Run();


// ----------------- Helpers DB (async) -----------------
static async Task GuardarMensajeAsync(string connectionString, string usuario, string texto)
{
    const int maxReintentos = 3;
    int intento = 0;

    while (intento < maxReintentos)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            var zonaColombia = TimeZoneInfo.FindSystemTimeZoneById("SA Pacific Standard Time");
            var fechaColombia = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zonaColombia);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Mensajes (Usuario, Texto, FechaHora) VALUES (@u, @t, @f)";
            cmd.Parameters.AddWithValue("@u", usuario);
            cmd.Parameters.AddWithValue("@t", texto);
            cmd.Parameters.AddWithValue("@f", fechaColombia);

            await cmd.ExecuteNonQueryAsync();
            return; // ✅ Éxito, salir del método
        }
        catch (SqlException ex) when (ex.Message.Contains("not currently available"))
        {
            intento++;
            Console.WriteLine($"⚠️ DB no disponible, reintento {intento}/{maxReintentos}...");
            await Task.Delay(3000 * intento); // espera incremental (3s, 6s, 9s)
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error guardando mensaje: {ex.Message}");
            return; // otros errores -> no reintentar
        }
    }

    Console.WriteLine("❌ No se pudo guardar el mensaje tras varios intentos.");
}



static async Task<string> ObtenerContextoAsync(string connectionString, string usuario, int limite = 10)
{
    try
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP (@l) Texto, FechaHora FROM Mensajes WHERE Usuario = @u ORDER BY Id DESC";
        cmd.Parameters.AddWithValue("@u", usuario);
        cmd.Parameters.AddWithValue("@l", limite);

        var mensajes = new List<(string Texto, DateTime FechaHora)>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var texto = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var fecha = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1);
            mensajes.Insert(0, (texto, fecha)); // insertamos al inicio para invertir el ORDER BY DESC
        }

        // Formatear la lista: "YYYY-MM-DD HH:mm - Texto"
        var lines = mensajes.Select(m => $"{m.FechaHora:yyyy-MM-dd HH:mm} - {m.Texto}");
        return string.Join("\n", lines);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error obteniendo contexto: {ex.Message}");
        return string.Empty;
    }
}

// Modelo - déjalo donde está
public class OcrRequest
{
    public long ChatId { get; set; }
    public string Texto { get; set; }
}

public class GeminiService
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;

    public GeminiService(string apiKey, HttpClient httpClient)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<string> ConsultarGemini(string contexto, string nuevoTexto)
    {
        try
        {
            string prompt = $"Contexto previo:\n{contexto}\n\nNueva entrada:\n{nuevoTexto}\n\nRespuesta:";

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={_apiKey}";

            var payload = new
            {
                contents = new[] {
                    new {
                        parts = new[] {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.7,
                    maxOutputTokens = 1024,
                    topP = 0.8,
                    topK = 40
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            return ProcesarRespuestaGemini(responseJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error en GeminiService: {ex.Message}");
            return "Lo siento, ocurrió un error al procesar la petición.";
        }
    }

    private string ProcesarRespuestaGemini(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            // Ajusta según la estructura real que recibas
            var text = root
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return text ?? "No se recibió respuesta";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error procesando respuesta Gemini: {ex.Message}");
            return "Error procesando la respuesta JSON";
        }
    }
}
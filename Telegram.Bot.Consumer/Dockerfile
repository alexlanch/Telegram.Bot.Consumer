# ============================
# Etapa 1: Compilación
# ============================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiamos el archivo .csproj y restauramos dependencias
COPY *.csproj ./
RUN dotnet restore

# Copiamos el resto del código fuente y compilamos
COPY . .
RUN dotnet publish -c Release -o /app/publish

# ============================
# Etapa 2: Runtime (ejecución)
# ============================
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copiamos la salida del build
COPY --from=build /app/publish .

# Si tu bot usa appsettings.json externo, puedes montar ese archivo con -v
# o dejarlo dentro del contenedor copiándolo así:
# COPY appsettings.json .

# Exponer puerto si tienes endpoints HTTP (opcional)
EXPOSE 8080

# Comando de inicio
ENTRYPOINT ["dotnet", "Telegram.Bot.Consumer.dll"]

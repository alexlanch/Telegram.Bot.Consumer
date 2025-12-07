using Microsoft.Data.SqlClient;

namespace TelegramBotApp.Savers
{
    public class UserSaverAsync
    {
        public static async Task GuardarUsuarioAsync(string connectionString, long id, string firstName, string lastName)
        {
            try
            {
                await using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
            IF NOT EXISTS (SELECT 1 FROM Usuarios WHERE UserId = @id)
            BEGIN
                INSERT INTO Usuarios (UserId, FirstName, LastName)
                VALUES (@id, @fn, @ln)
            END";

                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@fn", (object?)firstName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ln", (object?)lastName ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error guardando usuario: {ex.Message}");
            }
        }

    }
}

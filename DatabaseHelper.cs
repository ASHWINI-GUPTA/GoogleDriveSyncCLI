using Microsoft.Data.Sqlite;

namespace GoogleDriveSync;

/// <summary>
/// Helper class for database operations.
/// </summary>
public static class DatabaseHelper
{
    /// <summary>
    /// Initializes the database.
    /// </summary>
    public static async Task InitializeDatabase(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = @"
                CREATE TABLE IF NOT EXISTS SyncedFiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    LocalPath TEXT NOT NULL,
                    DriveId TEXT NOT NULL,
                    LastModified TEXT NOT NULL,
                    UNIQUE(LocalPath)
                )";
        await command.ExecuteNonQueryAsync();
    }
}
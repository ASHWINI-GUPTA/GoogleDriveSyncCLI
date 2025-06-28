using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System.CommandLine;

namespace GoogleDriveSync;

/// <summary>
/// CLI tool to sync local folders with Google Drive.
/// </summary>
class Program
{
    private static IConfiguration Config { get; set; }
    private static DriveService DriveService { get; set; }
    private static string DbPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sync.db");
    private static string UploadFolderId { get; set; }

    static async Task<int> Main(string[] args)
    {
        Config = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json")
            .Build();

        var rootCommand = new RootCommand("CLI tool to sync local folders with Google Drive");

        var folderOption = new Option<string>(
            ["--folder", "-f"],
            "The local folder path to sync with Google Drive")
        {
            IsRequired = true
        };

        var driveFolderOption = new Option<string>(
            ["--drive-folder-id", "-d"],
            "The Google Drive folder ID to sync with")
        {
            IsRequired = true
        };
                
        rootCommand.AddOption(folderOption);
        rootCommand.AddOption(driveFolderOption);

        rootCommand.SetHandler(async (folder, driveFolderId) =>
        {
            if (string.IsNullOrEmpty(driveFolderId))
            {
                Console.WriteLine("Error: Google Drive folder ID must be specified via --drive-folder-id");
                return;
            }

            UploadFolderId = driveFolderId;
            await DatabaseHelper.InitializeDatabase(DbPath);
            DriveService = await InitializeDriveService();
            await BidirectionalSync(folder);
        }, folderOption, driveFolderOption);

        return await rootCommand.InvokeAsync(args);
    }

    /// <summary>
    /// Initializes the Google Drive service.
    /// </summary>
    private static async Task<DriveService> InitializeDriveService()
    {
        await using var stream = new FileStream("client_secret.json", FileMode.Open, FileAccess.Read);
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            (await GoogleClientSecrets.FromStreamAsync(stream)).Secrets,
            [DriveService.Scope.Drive],
            "user",
            CancellationToken.None,
            new FileDataStore("token.json", true));

        return new DriveService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = "Google Drive Sync CLI",
        });
    }

    /// <summary>
    /// Performs bidirectional synchronization between local folder and Google Drive.
    /// </summary>
    private static async Task BidirectionalSync(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            Directory.CreateDirectory(folderPath);
        }

        Console.WriteLine($"Starting bidirectional sync between local folder: {folderPath} and Google Drive folder ID: {UploadFolderId}");
        
        await using var connection = new SqliteConnection($"Data Source={DbPath}");
        await connection.OpenAsync();

        // First, sync local changes to Drive
        await SyncLocalToDrive(folderPath, connection);

        // Then, sync Drive changes to local
        await SyncDriveToLocal(folderPath, connection);

        Console.WriteLine("Bidirectional sync completed.");
    }

    /// <summary>
    /// Syncs local folder changes to Google Drive
    /// </summary>
    private static async Task SyncLocalToDrive(string folderPath, SqliteConnection connection)
    {
        Console.WriteLine("Syncing local changes to Google Drive...");
        foreach (var filePath in Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories))
        {
            await SyncFileToDriver(connection, filePath);
        }
    }

    /// <summary>
    /// Syncs Google Drive changes to local folder
    /// </summary>
    private static async Task SyncDriveToLocal(string folderPath, SqliteConnection connection)
    {
        Console.WriteLine("Syncing Google Drive changes to local folder...");

        var request = DriveService.Files.List();
        request.Q = $"'{UploadFolderId}' in parents and trashed = false";
        request.Fields = "files(id, name, modifiedTime, md5Checksum)";

        var response = await request.ExecuteAsync();
        foreach (var file in response.Files)
        {
            await SyncFileToLocal(connection, file, folderPath);
        }
    }

    /// <summary>
    /// Syncs a single file from local to Google Drive.
    /// </summary>
    private static async Task SyncFileToDriver(SqliteConnection connection, string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        var relativePath = filePath[filePath.IndexOf(Path.DirectorySeparatorChar)..];

        var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT DriveId, LastModified FROM SyncedFiles WHERE LocalPath = $path";
        checkCmd.Parameters.AddWithValue("$path", relativePath);

        await using var reader = await checkCmd.ExecuteReaderAsync();
        string? driveId = null;
        var needsUpdate = true;

        if (reader.Read())
        {
            driveId = reader.GetString(0);
            var lastModified = DateTime.Parse(reader.GetString(1));
            needsUpdate = fileInfo.LastWriteTime > lastModified;
        }

        if (needsUpdate)
        {
            if (driveId == null)
            {
                // Upload new file
                var fileMetadata = new Google.Apis.Drive.v3.Data.File
                {
                    Name = Path.GetFileName(filePath),
                    Parents = [UploadFolderId]
                };

                await using var stream = new FileStream(filePath, FileMode.Open);
                var request = DriveService.Files.Create(fileMetadata, stream, MimeTypes.GetMimeType(filePath));
                await request.UploadAsync();
                driveId = request.ResponseBody.Id;

                Console.WriteLine($"Uploaded new file: {relativePath}");
            }
            else
            {
                // Update existing file
                var fileMetadata = new Google.Apis.Drive.v3.Data.File();
                await using var stream = new FileStream(filePath, FileMode.Open);
                var request = DriveService.Files.Update(fileMetadata, driveId, stream, MimeTypes.GetMimeType(filePath));
                await request.UploadAsync();

                Console.WriteLine($"Updated file: {relativePath}");
            }

            await UpdateDatabaseRecord(connection, relativePath, driveId, fileInfo.LastWriteTime);
        }
    }

    /// <summary>
    /// Syncs a single file from Google Drive to local.
    /// </summary>
    private static async Task SyncFileToLocal(SqliteConnection connection, Google.Apis.Drive.v3.Data.File driveFile, string folderPath)
    {
        var localPath = Path.Combine(folderPath, driveFile.Name);
        var relativePath = localPath[folderPath.Length..];

        var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT LastModified FROM SyncedFiles WHERE DriveId = $driveId";
        checkCmd.Parameters.AddWithValue("$driveId", driveFile.Id);

        await using var reader = await checkCmd.ExecuteReaderAsync();
        var needsUpdate = true;

        if (reader.Read())
        {
            var lastModified = DateTime.Parse(reader.GetString(0));
            needsUpdate = driveFile.ModifiedTime > lastModified;
        }

        if (needsUpdate)
        {
            var request = DriveService.Files.Get(driveFile.Id);
            await using var stream = new FileStream(localPath, FileMode.Create);
            await request.DownloadAsync(stream);

            Console.WriteLine($"Downloaded/Updated file from Drive: {relativePath}");
            await UpdateDatabaseRecord(connection, relativePath, driveFile.Id, driveFile.ModifiedTime.Value);
        }
    }

    /// <summary>
    /// Updates the database record for a synced file.
    /// </summary>
    private static async Task UpdateDatabaseRecord(SqliteConnection connection, string relativePath, string driveId, DateTime lastModified)
    {
        var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = @"
                INSERT OR REPLACE INTO SyncedFiles (LocalPath, DriveId, LastModified)
                VALUES ($path, $driveId, $modified)";
        updateCmd.Parameters.AddWithValue("$path", relativePath);
        updateCmd.Parameters.AddWithValue("$driveId", driveId);
        updateCmd.Parameters.AddWithValue("$modified", lastModified.ToString("o"));
        await updateCmd.ExecuteNonQueryAsync();
    }
}
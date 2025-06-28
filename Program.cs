using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Microsoft.Data.Sqlite;
using System.CommandLine;

namespace GoogleDriveSync;

/// <summary>
/// CLI tool to sync local folders with Google Drive.
/// </summary>
class Program
{
    /// <summary>
    /// Initializes the Google Drive service.
    /// </summary>
    private static DriveService? DriveService { get; set; }
    
    /// <summary>
    /// Gets the file path to the database used for synchronization.
    /// </summary>
    private static string DbPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sync.db");
    
    /// <summary>
    /// Gets or sets the unique identifier of the folder used for uploads.
    /// </summary>
    private static string? UploadFolderId { get; set; }

    static async Task<int> Main(string[] args)
    {
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

        var pullOnlyOption = new Option<bool>(
            ["--pull-only", "-p"],
            "Only pull from Drive, skip uploading local files to Drive")
        {
            IsRequired = false
        };

        var noDbOption = new Option<bool>(
            ["--no-db", "-n"],
            "Skip keeping database records of synced files")
        {
            IsRequired = false
        };
                
        rootCommand.AddOption(folderOption);
        rootCommand.AddOption(driveFolderOption);
        rootCommand.AddOption(pullOnlyOption);
        rootCommand.AddOption(noDbOption);

        rootCommand.SetHandler(async (folder, driveFolderId, pullOnly, noDb) =>
        {
            if (string.IsNullOrEmpty(driveFolderId))
            {
                Console.WriteLine("Error: Google Drive folder ID must be specified via --drive-folder-id");
                return;
            }

            UploadFolderId = driveFolderId;
            if (!noDb)
                await DatabaseHelper.InitializeDatabase(DbPath);
            DriveService = await InitializeDriveService();
            await BidirectionalSync(folder, pullOnly, noDb);
        }, folderOption, driveFolderOption, pullOnlyOption, noDbOption);

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
    private static async Task BidirectionalSync(string folderPath, bool pullOnly = false, bool noDb = false)
    {
        try
        {
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            Console.WriteLine($"Starting sync between local folder: {folderPath} and Google Drive folder ID: {UploadFolderId}");

            await using var connection = new SqliteConnection($"Data Source={DbPath}");
            await connection.OpenAsync();

            if (!pullOnly)
            {
                // First, sync local changes to Drive
                await SyncLocalToDrive(folderPath, connection, noDb);
            }
            else
            {
                Console.WriteLine("[Pull-Only Mode] Skipping upload to Drive.");
            }

            // Always sync Drive changes to local
            await SyncDriveToLocal(folderPath, connection, noDb);

            Console.WriteLine("Sync completed.");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error during sync: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Syncs local folder changes to Google Drive
    /// </summary>
    private static async Task SyncLocalToDrive(string folderPath, SqliteConnection connection, bool noDb)
    {
        try
        {
            Console.WriteLine("Syncing local changes to Google Drive...");
            var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories);
            int count = 0;
            foreach (var filePath in files)
            {
                count++;
                Console.Write($"[Uploading {count}/{files.Length}] ");
                await SyncFileToDriver(connection, filePath, noDb);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error syncing local to Drive: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Recursively syncs Google Drive changes to local folder, preserving folder structure.
    /// </summary>
    private static async Task SyncDriveToLocal(string folderPath, SqliteConnection connection, bool noDb)
    {
        try
        {
            Console.WriteLine("Syncing Google Drive changes to local folder (with folder structure)...");
            await SyncDriveFolderRecursive(UploadFolderId, folderPath, connection, noDb, 0);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error syncing Drive to local: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Recursively syncs a Drive folder and its contents to the local folder.
    /// </summary>
    private static async Task SyncDriveFolderRecursive(string driveFolderId, string localFolderPath, SqliteConnection connection, bool noDb, int indentLevel)
    {
        try
        {
            string indent = new string(' ', indentLevel * 2);
            if (!Directory.Exists(localFolderPath))
                Directory.CreateDirectory(localFolderPath);

            var request = DriveService.Files.List();
            request.Q = $"'{driveFolderId}' in parents and trashed = false";
            request.Fields = "files(id, name, mimeType, modifiedTime, md5Checksum, size)";
            var response = await request.ExecuteAsync();

            Console.WriteLine($"{indent}Folder: {Path.GetFileName(localFolderPath)}");
            int count = 0;
            foreach (var file in response.Files)
            {
                count++;
                if (file.MimeType == "application/vnd.google-apps.folder")
                {
                    await SyncDriveFolderRecursive(file.Id, Path.Combine(localFolderPath, file.Name), connection, noDb, indentLevel + 1);
                }
                else
                {
                    Console.Write($"{indent}  [Downloading {count}/{response.Files.Count}] ");
                    await SyncFileToLocal(connection, file, localFolderPath, noDb, indentLevel + 1);
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error syncing Drive folder: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Syncs a single file from local to Google Drive.
    /// </summary>
    private static async Task SyncFileToDriver(SqliteConnection connection, string filePath, bool noDb)
    {
        try
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
                    var fileMetadata = new Google.Apis.Drive.v3.Data.File
                    {
                        Name = Path.GetFileName(filePath),
                        Parents = [UploadFolderId]
                    };

                    await using var stream = new FileStream(filePath, FileMode.Open);
                    var request = DriveService.Files.Create(fileMetadata, stream, MimeTypes.GetMimeType(filePath));
                    long total = stream.Length;
                    request.ProgressChanged += (progress) =>
                    {
                        DrawProgressBar(progress.BytesSent, total, "Uploading");
                    };
                    await request.UploadAsync();
                    driveId = request.ResponseBody.Id;
                    Console.WriteLine($"\nUploaded new file: {relativePath}");
                }
                else
                {
                    var fileMetadata = new Google.Apis.Drive.v3.Data.File();
                    await using var stream = new FileStream(filePath, FileMode.Open);
                    var request = DriveService.Files.Update(fileMetadata, driveId, stream, MimeTypes.GetMimeType(filePath));
                    long total = stream.Length;
                    request.ProgressChanged += (progress) =>
                    {
                        DrawProgressBar(progress.BytesSent, total, "Updating");
                    };
                    await request.UploadAsync();
                    Console.WriteLine($"\nUpdated file: {relativePath}");
                }

                if (!noDb)
                    await UpdateDatabaseRecord(connection, relativePath, driveId, fileInfo.LastWriteTime);
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error uploading file '{filePath}': {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Syncs a single file from Google Drive to local.
    /// </summary>
    private static async Task SyncFileToLocal(SqliteConnection connection, Google.Apis.Drive.v3.Data.File driveFile, string folderPath, bool noDb, int indentLevel)
    {
        try
        {
            string indent = new string(' ', indentLevel * 2);
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
                needsUpdate = driveFile.ModifiedTimeDateTimeOffset > lastModified;
            }

            if (needsUpdate)
            {
                var request = DriveService.Files.Get(driveFile.Id);
                long total = driveFile.Size ?? 0;
                request.MediaDownloader.ProgressChanged += (progress) =>
                {
                    Console.Write($"\r{indent}");
                    DrawProgressBar((long)progress.BytesDownloaded, total, "Downloading");
                };
                await using var stream = new FileStream(localPath, FileMode.Create);
                await request.DownloadAsync(stream);
                Console.WriteLine($"\n{indent}Downloaded/Updated file from Drive: {relativePath}");
                if (!noDb)
                {
                    await UpdateDatabaseRecord(connection, relativePath, driveFile.Id, driveFile.ModifiedTimeDateTimeOffset.Value.DateTime);
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error downloading file '{driveFile.Name}': {ex.Message}");
            Console.ResetColor();
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

    /// <summary>
    /// Draws a progress bar in the console.
    /// </summary>
    private static void DrawProgressBar(long current, long total, string label)
    {
        const int barWidth = 40;
        double percent = total > 0 ? (double)current / total : 0;
        int filled = (int)(percent * barWidth);
        string bar = new string('=', filled) + new string(' ', barWidth - filled);
        string sizeStr = $"{FormatSize(current)} / {FormatSize(total)}";
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"\r{label} [{bar}] {percent * 100,6:F2}% {sizeStr}   ");
        Console.ResetColor();
    }

    /// <summary>
    /// Formats a size in bytes to a human-readable string.
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes > 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        if (bytes > 1024)
            return $"{bytes / 1024.0:F2} KB";
        return $"{bytes} B";
    }
}
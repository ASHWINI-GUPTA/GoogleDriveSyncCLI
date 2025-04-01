using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System.CommandLine;

namespace GoogleDriveSync;

/// <summary>
/// CLI tool to sync local folders with Google Drive.
/// </summary>
class Program
{
    /// <summary>
    /// Google Drive service instance.
    /// </summary>
    private static DriveService? DriveService { get; set; }

    /// <summary>
    /// Mapping of local folder paths to their corresponding Google Drive folder IDs.
    /// </summary>
    private static readonly Dictionary<string, string> folderIdMap = [];

    /// <summary>
    /// Entry point of the application.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Exit code.</returns>
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
            "The Google Drive folder ID to upload to")
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

            DriveService = await InitializeDriveServiceAsync();

            await UploadDirectory(folder, driveFolderId);
        }, folderOption, driveFolderOption);

        return await rootCommand.InvokeAsync(args);
    }

    /// <summary>
    /// Initializes the Google Drive service.
    /// </summary>
    /// <returns>An instance of <see cref="DriveService"/>.</returns>
    private static async Task<DriveService> InitializeDriveServiceAsync()
    {
        string credPath = "token.json";

        if (File.Exists(credPath))
        {
            File.Delete(credPath);
        }

        using var stream = new FileStream("client_secret.json", FileMode.Open, FileAccess.Read);

        // Define the scopes required for your app (e.g., access to Google Drive)
        string[] scopes = [DriveService.Scope.Drive];

        // This will trigger the OAuth authorization flow
        var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            GoogleClientSecrets.FromStream(stream).Secrets,
            scopes,
            "ashwini@fnlsg.in",  // Unique identifier for the user
            CancellationToken.None,
            new FileDataStore("GoogleDriveSyncCLI", true) // Store credentials locally for future use
        );

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Google Drive Sync CLI",
            HttpClientTimeout = TimeSpan.FromDays(1)
        });

    }

    /// <summary>
    /// Checks if a file exists in the specified Google Drive folder.
    /// </summary>
    /// <param name="fileName">The name of the file to check.</param>
    /// <param name="parentFolderId">The ID of the parent folder in Google Drive.</param>
    /// <returns>The file ID if it exists, otherwise <c>null</c>.</returns>
    static async Task<string?> CheckIfFileExists(string fileName, string parentFolderId)
    {
        var request = DriveService!.Files.List();
        request.Q = $"name = '{fileName}' and '{parentFolderId}' in parents";
        request.Fields = "files(id)";
        var response = await request.ExecuteAsync();

        if (response.Files.Any())
        {
            return response.Files.First().Id; // File exists
        }

        return null; // File does not exist
    }

    /// <summary>
    /// Uploads a file to Google Drive.
    /// </summary>
    /// <param name="filePath">The local file path.</param>
    /// <param name="parentFolderId">The ID of the parent folder in Google Drive.</param>
    static async Task UploadFileToDrive(string filePath, string parentFolderId)
    {
        string fileName = Path.GetFileName(filePath);
        var existingFileId = await CheckIfFileExists(fileName, parentFolderId);

        if (existingFileId != null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Exist - {fileName} into Folder {GetLastChildDirectory(filePath)}");
            Console.ResetColor();
            return;
        }

        try
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            
            var fileInfo = new FileInfo(filePath);
            Console.WriteLine($"Uploading - {fileName} ({fileInfo.Length / (1024.0 * 1024.0):F2} MB) into Folder {GetLastChildDirectory(filePath)}");

            var fileMetadata = new Google.Apis.Drive.v3.Data.File
            {
                Name = Path.GetFileName(filePath),
                Parents = [parentFolderId]
            };

            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            var request = DriveService!.Files.Create(fileMetadata, fileStream, MimeTypes.GetMimeType(filePath));
            request.Fields = "id";
            request.SupportsAllDrives = true;

            var progress = await request.UploadAsync();

            if (progress.Status == Google.Apis.Upload.UploadStatus.Completed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Uploaded - {fileName} into Folder {GetLastChildDirectory(filePath)}");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed - {fileName} into Folder {GetLastChildDirectory(filePath)}");
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed - {fileName} into Folder {GetLastChildDirectory(filePath)} | Error: {ex.Message}");
        }
        finally
        {
            Console.ResetColor();
        }
    }

    private static string GetLastChildDirectory(string filePath)
    {
        return new DirectoryInfo(Path.GetDirectoryName(filePath)!).Name;
    }

    /// <summary>
    /// Recursively uploads a directory and its contents to Google Drive.
    /// </summary>
    /// <param name="localFolderPath">The local folder path.</param>
    /// <param name="driveParentFolderId">The ID of the parent folder in Google Drive.</param>
    static async Task UploadDirectory(string localFolderPath, string driveParentFolderId)
    {
        foreach (var dir in Directory.GetDirectories(localFolderPath))
        {
            string folderName = Path.GetFileName(dir);

            string parentFolderId = folderIdMap.TryGetValue(localFolderPath, out var id) ? id : driveParentFolderId;
            string folderId = await CreateFolderIfNotExists(folderName, parentFolderId);
            folderIdMap[dir] = folderId;

            await UploadDirectory(dir, folderId);
        }

        foreach (var file in Directory.GetFiles(localFolderPath))
        {
            await UploadFileToDrive(file, folderIdMap.TryGetValue(localFolderPath, out var id) ? id : driveParentFolderId);
        }
    }

    /// <summary>
    /// Creates a folder in Google Drive if it does not already exist.
    /// </summary>
    /// <param name="folderName">The name of the folder to create.</param>
    /// <param name="parentFolderId">The ID of the parent folder in Google Drive.</param>
    /// <returns>The ID of the created or existing folder.</returns>
    static async Task<string> CreateFolderIfNotExists(string folderName, string parentFolderId)
    {
        var request = DriveService!.Files.List();
        
        request.Q = $"mimeType='application/vnd.google-apps.folder' and name='{folderName}' and '{parentFolderId}' in parents and trashed = false";
        request.IncludeItemsFromAllDrives = true;
        request.SupportsAllDrives = true;

        request.Fields = "files(id, name)";
        var response = await request.ExecuteAsync();

        if (response.Files.Any())
        {
            return response.Files.First().Id;
        }

        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = folderName,
            MimeType = "application/vnd.google-apps.folder",
            Parents = [parentFolderId]
        };

        var requestCreate = DriveService.Files.Create(fileMetadata);
        requestCreate.Fields = "id";
        var folder = await requestCreate.ExecuteAsync();

        return folder.Id;
    }
}
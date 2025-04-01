# Google Drive Sync CLI

GoogleDriveSync is a CLI tool to sync local folders with Google Drive. It allows users to upload files and directories to Google Drive while maintaining folder structures.

## Features
- Upload files and directories to Google Drive.
- Automatically create folders in Google Drive if they do not exist.
- Check for existing files in Google Drive to avoid duplicates.

## Prerequisites
- .NET 9.0 or later.
- A Google Cloud project with the Google Drive API enabled.
- A `client_secret.json` file for OAuth 2.0 credentials.

## Installation
1. Clone the repository:
   ```bash
   git clone https://github.com/ASHWINI-GUPTA/GoogleDriveSyncCLI.git
   ```
2. Navigate to the project directory:
   ```bash
   cd GoogleDriveSyncCLI
   ```
3. Restore dependencies:
   ```bash
   dotnet restore
   ```

## Usage
Run the application with the following command:
```bash
dotnet run -- --folder "<local-folder-path>" --drive-folder-id "<google-drive-folder-id>"
```

### Options
- `--folder` or `-f`: The local folder path to sync with Google Drive.
- `--drive-folder-id` or `-d`: The Google Drive folder ID to upload to.

## Example
```bash
dotnet run -- --folder "G:\\MyFiles" --drive-folder-id "1a2b3c4d5e"
```

## License
This project is licensed under the Apache License 2.0. See the [LICENSE](LICENSE.txt) file for details.
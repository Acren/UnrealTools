using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PluginDeploy
{
    class DriveIntegration
    {
        static string[] Scopes = { DriveService.Scope.Drive };
        static string ApplicationName = "Markatplace Deploy";

        public static void UploadFile(string FilePath)
        {
            UserCredential Credential;

            using (var stream =
                new FileStream("google_client_secret.json", FileMode.Open, FileAccess.Read))
            {
                string credPath = System.Environment.GetFolderPath(
                    System.Environment.SpecialFolder.Personal);
                credPath = Path.Combine(credPath, ".credentials/marketplace-deploy-drive.json");

                Credential = GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.Load(stream).Secrets,
                    Scopes,
                    "user",
                    CancellationToken.None,
                    new FileDataStore(credPath, true)).Result;
                Console.WriteLine("Credential file saved to: " + credPath);
            }

            DriveService Service = new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = Credential,
                ApplicationName = ApplicationName,
            });

            Service.HttpClient.Timeout = TimeSpan.FromMinutes(300);

            // Try to find folder
            FilesResource.ListRequest FolderSearchRequest = Service.Files.List();
            FolderSearchRequest.Q = "name='MarketplaceStaging'";
            FileList FolderSearchResult = FolderSearchRequest.Execute();

            string ParentFolderId;

            if(FolderSearchResult.Files.Count > 0)
            {
                // Folder exists so use it
                ParentFolderId = FolderSearchResult.Files[0].Id;
            }
            else
            {
                // Folder doesn't exist so we should create it
                Google.Apis.Drive.v3.Data.File FolderMetaData = new Google.Apis.Drive.v3.Data.File()
                {
                    Name = "MarketplaceStaging",
                    MimeType = "application/vnd.google-apps.folder"
                };

                FilesResource.CreateRequest CreateFolderRequest = Service.Files.Create(FolderMetaData);
                CreateFolderRequest.Fields = "id";
                Google.Apis.Drive.v3.Data.File FolderFile = CreateFolderRequest.Execute();
                ParentFolderId = FolderFile.Id;
            }

            // Upload file into folder
            Google.Apis.Drive.v3.Data.File MetaData = new Google.Apis.Drive.v3.Data.File()
            {
                Name = Path.GetFileName(FilePath),
                Parents = new List<string> { ParentFolderId }
            };

            FileStream Stream = new FileStream(FilePath, FileMode.Open);

            FilesResource.CreateMediaUpload Request = Service.Files.Create(MetaData, Stream, "application/x-zip-compressed");
            Request.Fields = "id";
            Request.Upload();

            Google.Apis.Drive.v3.Data.File Response = Request.ResponseBody;
        }

    }
}

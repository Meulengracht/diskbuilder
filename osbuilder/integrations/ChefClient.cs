using Azure;
using Azure.Storage.Blobs;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;

namespace OSBuilder.Integrations
{
    internal class ChefClient
    {
        static readonly string ChefApiBaseUrl = "https://chef-api.azurewebsites.net/api";

        static private string GetDownloadUrl(string publisher, string name, string platform, string arch, string channel)
        {
            return Uri.EscapeUriString($"{ChefApiBaseUrl}/pack/download?publisher={publisher}&name={name}&platform={platform}&arch={arch}&channel={channel}");
        }
        
        static async private Task DownloadAsync(string fullSasUrl, string path)
        {
            var tokens = fullSasUrl.Split('?');
            if (tokens.Length != 2)
                throw new Exception($"{nameof(ChefClient)} | {nameof(DownloadAsync)} | ERROR: Invalid sas url: {fullSasUrl}");
            
            var blobClient = new BlobClient(new Uri(tokens[0]), new AzureSasCredential(tokens[1]));
            if (!blobClient.Exists())
                throw new Exception($"{nameof(ChefClient)} | {nameof(DownloadAsync)} | ERROR: Blob does not exist: {fullSasUrl}");

            var blob = blobClient.DownloadStreaming().Value;
            using (var fileStream = File.Create(path))
            {
                var buffer = new byte[81920];
                using (var progress = new Utils.ProgressBar())
                {
                    long totalBytesDownloaded = 0;
                    long totalBytes = blob.Details.ContentLength;
                    progress.Report(0);
                    do
                    {
                        int bytesRead = await blob.Content.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0)
                            break;

                        await fileStream.WriteAsync(buffer, 0, bytesRead);

                        totalBytesDownloaded += bytesRead;

                        progress.Report((double)totalBytesDownloaded / (double)totalBytes);
                    } while (true);
                    progress.Report(1);
                }
            }
            Console.WriteLine("Success");
        }

        static public async Task DownloadPack(string path, string package, string platform, string arch, string channel)
        {
            var tokens = package.Split('/');
            if (tokens.Length != 2)
                throw new Exception($"{nameof(ChefClient)} | {nameof(DownloadPack)} | ERROR: Invalid package: {package}");
            
            var publisher = tokens[0];
            var name = tokens[1];
            var downloadUrl = GetDownloadUrl(publisher, name, platform, arch, channel);

            // create the http client
            using (var httpClient = new HttpClient())
            {
                // get the download instructions and deserialize from json
                var response = await httpClient.GetAsync(downloadUrl);
                if (!response.IsSuccessStatusCode)
                    throw new Exception($"{nameof(ChefClient)} | {nameof(DownloadPack)} | ERROR: Failed to find chef package");

                var body = await response.Content.ReadAsStringAsync();
                var downloadResponse = JsonSerializer.Deserialize<ChefDownloadResponse>(body);
                if (downloadResponse == null)
                    throw new Exception($"{nameof(ChefClient)} | {nameof(DownloadPack)} | ERROR: Invalid download response: {body}");

                await DownloadAsync(downloadResponse.BlobUrl, path);
            }
        }
    }
}

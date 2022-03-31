using System.Text.Json.Serialization;

namespace OSBuilder.Integrations
{
    class ChefDownloadResponse
    {
        [JsonPropertyName("sas-token")]
        public string SasToken { get; set; }

        [JsonPropertyName("blob-url")]
        public string BlobUrl { get; set; }
    }
}

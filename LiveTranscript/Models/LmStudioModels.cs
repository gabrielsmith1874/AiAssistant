using System.Collections.Generic;
using Newtonsoft.Json;

namespace LiveTranscript.Models
{
    public class LmStudioModelListResponse
    {
        [JsonProperty("data")]
        public List<LmStudioModelRaw> Data { get; set; } = new();
    }

    public class LmStudioModelRaw
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("object")]
        public string? Object { get; set; }

        [JsonProperty("context_length")]
        public int? ContextLength { get; set; }

        [JsonProperty("max_context_length")]
        public int? MaxContextLength { get; set; }
    }
}

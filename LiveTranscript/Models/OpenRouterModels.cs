using System.Collections.Generic;
using Newtonsoft.Json;

namespace LiveTranscript.Models
{
    // ── OpenRouter /api/v1/models response ──

    public class ModelListResponse
    {
        [JsonProperty("data")]
        public List<OpenRouterModelRaw> Data { get; set; } = new();
    }

    public class OpenRouterModelRaw
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("context_length")]
        public int ContextLength { get; set; }

        [JsonProperty("architecture")]
        public ModelArchitecture? Architecture { get; set; }

        [JsonProperty("pricing")]
        public ModelPricing? Pricing { get; set; }
    }

    public class ModelArchitecture
    {
        [JsonProperty("input_modalities")]
        public List<string>? InputModalities { get; set; }

        [JsonProperty("output_modalities")]
        public List<string>? OutputModalities { get; set; }

        [JsonProperty("tokenizer")]
        public string? Tokenizer { get; set; }
    }

    public class ModelPricing
    {
        [JsonProperty("prompt")]
        public string? Prompt { get; set; }

        [JsonProperty("completion")]
        public string? Completion { get; set; }
    }

    // ── Processed model for UI display ──

    public class OpenRouterModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int ContextLength { get; set; }
        public double PromptPrice { get; set; }   // per million tokens
        public double CompletionPrice { get; set; }
        public bool IsFree => PromptPrice == 0 && CompletionPrice == 0;
        public string PriceDisplay => IsFree ? "Free" : $"${PromptPrice:F4}/{CompletionPrice:F4}";
        public string ContextDisplay => ContextLength >= 1000 ? $"{ContextLength / 1000}K" : ContextLength.ToString();
        public string ParameterCount { get; set; } = string.Empty;

        public static OpenRouterModel FromRaw(OpenRouterModelRaw raw)
        {
            double.TryParse(raw.Pricing?.Prompt ?? "0", System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double promptPrice);
            double.TryParse(raw.Pricing?.Completion ?? "0", System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double completionPrice);

            // API returns price per token, convert to per-million
            promptPrice *= 1_000_000;
            completionPrice *= 1_000_000;

            // Extract parameter count from name (e.g. "7B", "70B", "8x7B")
            var match = System.Text.RegularExpressions.Regex.Match(raw.Name, @"\b(\d+x)?\d+B\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string paramsDisplay = match.Success ? match.Value.ToUpper() : "";

            return new OpenRouterModel
            {
                Id = raw.Id,
                Name = raw.Name,
                ContextLength = raw.ContextLength,
                PromptPrice = promptPrice,
                CompletionPrice = completionPrice,
                ParameterCount = paramsDisplay
            };
        }
    }

    // ── Chat completion request/response ──

    public class ChatCompletionRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; } = string.Empty;

        [JsonProperty("messages")]
        public List<ChatMessage> Messages { get; set; } = new();
    }

    public class ChatMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; } = string.Empty;

        [JsonProperty("content")]
        public string Content { get; set; } = string.Empty;
    }

    public class ChatCompletionResponse
    {
        [JsonProperty("choices")]
        public List<ChatChoice>? Choices { get; set; }
    }

    public class ChatChoice
    {
        [JsonProperty("message")]
        public ChatMessage? Message { get; set; }
    }
}

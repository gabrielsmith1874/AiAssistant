using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveTranscript.Models;
using Newtonsoft.Json;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Handles Anthropic Claude API requests for interview Q&A extraction.
    /// </summary>
    public class ClaudeService
    {
        private const string CompletionsUrl = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";
        private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(1250);
        private static readonly SemaphoreSlim RequestRateGate = new(1, 1);
        private static DateTimeOffset _nextRequestAtUtc = DateTimeOffset.MinValue;

        private readonly HttpClient _httpClient;

        public ClaudeService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        }

        public async Task<List<ExtractedQuestion>> ExtractQuestionAnswersAsync(
            string apiKey,
            string modelId,
            string transcript,
            IEnumerable<string>? knownQuestions,
            string jobDescription,
            string resume,
            string? answerHistory,
            bool useJotNotes)
        {
            var request = new ClaudeCompletionRequest
            {
                Model = modelId,
                MaxTokens = 4096,
                System = AiPromptTemplates.BuildQuestionAnswerExtractionSystemPrompt(
                    jobDescription, resume, useJotNotes),
                Thinking = new ClaudeThinkingConfig { Type = "disabled" },
                Messages = new List<ClaudeMessage>
                {
                    new()
                    {
                        Role = "user",
                        Content = AiPromptTemplates.BuildQuestionAnswerExtractionUserPrompt(
                            transcript, knownQuestions, answerHistory)
                    }
                }
            };

            var json = JsonConvert.SerializeObject(request, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, CompletionsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Add("x-api-key", apiKey);

            var response = await SendClaudeRequestAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Claude extraction/answer error ({response.StatusCode}): {responseText}");

            var result = JsonConvert.DeserializeObject<ClaudeCompletionResponse>(responseText);
            var text = result?.Content?.FirstOrDefault()?.Text ?? "[]";

            return ExtractedQuestionParser.Parse(text);
        }

        private async Task<HttpResponseMessage> SendClaudeRequestAsync(HttpRequestMessage request)
        {
            Task<HttpResponseMessage> sendTask;
            await RequestRateGate.WaitAsync();
            try
            {
                await WaitForClaudeRequestSlotAsync();
                sendTask = _httpClient.SendAsync(request);
                _nextRequestAtUtc = DateTimeOffset.UtcNow + MinimumRequestInterval;
            }
            finally
            {
                RequestRateGate.Release();
            }

            return await sendTask;
        }

        private static async Task WaitForClaudeRequestSlotAsync()
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _nextRequestAtUtc)
                await Task.Delay(_nextRequestAtUtc - now);
        }
    }

    public class ClaudeCompletionRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; } = string.Empty;

        [JsonProperty("max_tokens")]
        public int MaxTokens { get; set; } = 4096;

        [JsonProperty("system")]
        public string System { get; set; } = string.Empty;

        [JsonProperty("thinking")]
        public ClaudeThinkingConfig? Thinking { get; set; }

        [JsonProperty("messages")]
        public List<ClaudeMessage> Messages { get; set; } = new();
    }

    public class ClaudeThinkingConfig
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "disabled";
    }

    public class ClaudeMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; } = string.Empty;

        [JsonProperty("content")]
        public string Content { get; set; } = string.Empty;
    }

    public class ClaudeCompletionResponse
    {
        [JsonProperty("content")]
        public List<ClaudeContentBlock>? Content { get; set; }
    }

    public class ClaudeContentBlock
    {
        [JsonProperty("text")]
        public string? Text { get; set; }
    }
}

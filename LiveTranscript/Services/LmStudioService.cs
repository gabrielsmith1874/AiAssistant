using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LiveTranscript.Models;
using Newtonsoft.Json;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Handles LM Studio OpenAI-compatible API endpoints.
    /// </summary>
    public class LmStudioService
    {
        private readonly HttpClient _httpClient;

        public LmStudioService()
        {
            _httpClient = new HttpClient();
        }

        public async Task<List<OpenRouterModel>> FetchModelsAsync(string baseUrl, string apiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{NormalizeBaseUrl(baseUrl)}/v1/models");
            AddOptionalAuthHeader(request, apiKey);

            var response = await _httpClient.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"LM Studio models error ({response.StatusCode}): {responseText}");

            var parsed = JsonConvert.DeserializeObject<LmStudioModelListResponse>(responseText);
            if (parsed?.Data == null)
                return new List<OpenRouterModel>();

            return parsed.Data
                .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                .Select(m =>
                {
                    var modelId = m.Id.Trim();
                    var match = Regex.Match(modelId, @"\b(\d+x)?\d+B\b", RegexOptions.IgnoreCase);
                    return new OpenRouterModel
                    {
                        Id = modelId,
                        Name = modelId,
                        ContextLength = m.ContextLength ?? m.MaxContextLength ?? 0,
                        PromptPrice = 0,
                        CompletionPrice = 0,
                        ParameterCount = match.Success ? match.Value.ToUpperInvariant() : string.Empty
                    };
                })
                .OrderByDescending(m => m.ContextLength)
                .ThenBy(m => m.Name)
                .ToList();
        }

        public async Task WarmupAsync(string baseUrl, string apiKey, string modelId)
        {
            var request = new
            {
                model = modelId,
                max_tokens = 1,
                temperature = 0,
                messages = new[]
                {
                    new { role = "user", content = "ok" }
                }
            };

            var json = JsonConvert.SerializeObject(request);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeBaseUrl(baseUrl)}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddOptionalAuthHeader(httpRequest, apiKey);

            var response = await _httpClient.SendAsync(httpRequest);
            _ = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"LM Studio warmup error ({response.StatusCode})");
        }

        public Task<List<ExtractedQuestion>> ExtractQuestionAnswersAsync(
            string baseUrl,
            string apiKey,
            string modelId,
            string transcript,
            IEnumerable<string>? knownQuestions,
            string jobDescription,
            string resume,
            string? answerHistory,
            bool useJotNotes)
        {
            return OpenAiCompatibleChatService.ExtractQuestionAnswersAsync(
                _httpClient,
                $"{NormalizeBaseUrl(baseUrl)}/v1/chat/completions",
                request => AddOptionalAuthHeader(request, apiKey),
                modelId,
                transcript,
                knownQuestions,
                jobDescription,
                resume,
                answerHistory,
                useJotNotes,
                "LM Studio extraction/answer");
        }

        private static void AddOptionalAuthHeader(HttpRequestMessage request, string apiKey)
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Add("Authorization", $"Bearer {apiKey.Trim()}");
        }

        private static string NormalizeBaseUrl(string baseUrl)
        {
            var trimmed = (baseUrl ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return "http://127.0.0.1:1234";
            return trimmed.TrimEnd('/');
        }
    }
}

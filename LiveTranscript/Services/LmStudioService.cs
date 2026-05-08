using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LiveTranscript.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Handles LM Studio OpenAI-compatible API endpoints.
    /// Tracks previously answered questions to avoid duplicates.
    /// </summary>
    public class LmStudioService
    {
        private readonly HttpClient _httpClient;

        public List<string> PreviouslyAnswered { get; } = new();

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

        public async Task<List<ExtractedQuestion>> ExtractQuestionTextsOnlyAsync(
            string baseUrl, string apiKey, string modelId,
            string transcript, IEnumerable<string>? knownQuestions = null)
        {
            var systemPrompt = AiPromptTemplates.BuildQuestionExtractionSystemPrompt();
            var userPrompt = AiPromptTemplates.BuildQuestionExtractionUserPrompt(transcript, knownQuestions);

            var request = new ChatCompletionRequest
            {
                Model = modelId,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = systemPrompt },
                    new() { Role = "user", Content = userPrompt }
                }
            };

            var json = JsonConvert.SerializeObject(request);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeBaseUrl(baseUrl)}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddOptionalAuthHeader(httpRequest, apiKey);

            var response = await _httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"LM Studio extraction error ({response.StatusCode}): {responseText}");

            var result = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseText);
            var text = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "[]";

            try
            {
                var parsed = JsonConvert.DeserializeObject<List<ExtractedQuestionDto>>(text) ?? new List<ExtractedQuestionDto>();
                return parsed
                    .Where(x => !string.IsNullOrWhiteSpace(x.Q))
                    .Select(x => new ExtractedQuestion
                    {
                        Question = x.Q!.Trim(),
                        IsFollowUp = x.F ?? false,
                        ParentQuestion = (x.P ?? string.Empty).Trim()
                    })
                    .ToList();
            }
            catch
            {
                var match = Regex.Match(text, @"\[.*\]", RegexOptions.Singleline);
                if (!match.Success)
                    return new List<ExtractedQuestion>();

                var parsed = JsonConvert.DeserializeObject<List<ExtractedQuestionDto>>(match.Value) ?? new List<ExtractedQuestionDto>();
                return parsed
                    .Where(x => !string.IsNullOrWhiteSpace(x.Q))
                    .Select(x => new ExtractedQuestion
                    {
                        Question = x.Q!.Trim(),
                        IsFollowUp = x.F ?? false,
                        ParentQuestion = (x.P ?? string.Empty).Trim()
                    })
                    .ToList();
            }
        }

        public async IAsyncEnumerable<string> StreamAnswerAsync(
            string baseUrl, string apiKey, string modelId,
            string question, string transcript, string jobDescription, string resume,
            string? parentQuestion = null, string? parentAnswer = null)
        {
            var systemPrompt = AiPromptTemplates.BuildAnswerSystemPrompt(jobDescription, resume);
            var userPrompt = AiPromptTemplates.BuildAnswerUserPrompt(
                question, transcript, parentQuestion, parentAnswer);

            var request = new
            {
                model = modelId,
                stream = true,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };

            var json = JsonConvert.SerializeObject(request);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{NormalizeBaseUrl(baseUrl)}/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AddOptionalAuthHeader(httpRequest, apiKey);

            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                yield return $"[Error: {error}]";
                yield break;
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var body = await response.Content.ReadAsStringAsync();
                var parsed = JsonConvert.DeserializeObject<ChatCompletionResponse>(body);
                var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
                yield break;
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[8192];
            var leftover = string.Empty;

            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0) break;

                var text = leftover + Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var lines = text.Split('\n');
                leftover = lines[^1];

                for (int i = 0; i < lines.Length - 1; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                        continue;

                    var data = line.Substring(6).Trim();
                    if (data == "[DONE]")
                        yield break;

                    string? contentToYield = null;
                    try
                    {
                        var delta = JsonConvert.DeserializeObject<JObject>(data);
                        contentToYield = delta?["choices"]?.FirstOrDefault()?["delta"]?["content"]?.ToString();
                    }
                    catch
                    {
                        // Ignore malformed stream chunk and continue.
                    }

                    if (!string.IsNullOrEmpty(contentToYield))
                        yield return contentToYield;
                }
            }
        }

        public void ClearHistory() => PreviouslyAnswered.Clear();

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

        private class ExtractedQuestionDto
        {
            [JsonProperty("q")]
            public string? Q { get; set; }

            [JsonProperty("f")]
            public bool? F { get; set; }

            [JsonProperty("p")]
            public string? P { get; set; }
        }
    }
}

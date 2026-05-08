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
    /// Handles OpenRouter API: model listing and chat completions.
    /// Tracks previously answered questions to avoid duplicates.
    /// </summary>
    public class OpenRouterService
    {
        private const string ModelsUrl = "https://openrouter.ai/api/v1/models";
        private const string CompletionsUrl = "https://openrouter.ai/api/v1/chat/completions";

        private readonly HttpClient _httpClient;

        /// <summary>
        /// Accumulates Q&A pairs from previous extractions so the LLM
        /// skips already-answered questions on subsequent calls.
        /// </summary>
        public List<string> PreviouslyAnswered { get; } = new();

        public OpenRouterService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/live-transcript");
            _httpClient.DefaultRequestHeaders.Add("X-Title", "Live Transcript");
        }

        /// <summary>
        /// Fetches the full model list from OpenRouter.
        /// </summary>
        public async Task<List<OpenRouterModel>> FetchModelsAsync()
        {
            var response = await _httpClient.GetStringAsync(ModelsUrl);
            var raw = JsonConvert.DeserializeObject<ModelListResponse>(response);
            if (raw?.Data == null) return new List<OpenRouterModel>();

            return raw.Data
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .Select(OpenRouterModel.FromRaw)
                .OrderByDescending(m => m.ContextLength)
                .ToList();
        }

        public async Task<List<ExtractedQuestion>> ExtractQuestionTextsOnlyAsync(
            string apiKey, string modelId,
            string transcript,
            IEnumerable<string>? knownQuestions = null)
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
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, CompletionsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");

            var response = await _httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"OpenRouter API error ({response.StatusCode}): {responseText}");

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
            string apiKey, string modelId,
            string question, string transcript, string jobDescription, string resume,
            string? parentQuestion = null, string? parentAnswer = null)
        {
            var request = new ChatCompletionRequest
            {
                Model = modelId,
                Messages = new List<ChatMessage>
                {
                    new() { Role = "system", Content = AiPromptTemplates.BuildAnswerSystemPrompt(jobDescription, resume) },
                    new() { Role = "user", Content = AiPromptTemplates.BuildAnswerUserPrompt(question, transcript, parentQuestion, parentAnswer) }
                }
            };

            var json = JsonConvert.SerializeObject(request);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, CompletionsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");

            var response = await _httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                yield return $"[Error: {responseText}]";
                yield break;
            }

            var result = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseText);
            var answer = result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(answer))
            {
                yield return answer;
            }
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

        public void ClearHistory() => PreviouslyAnswered.Clear();
    }
}

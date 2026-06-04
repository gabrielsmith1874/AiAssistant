using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using LiveTranscript.Models;
using Newtonsoft.Json;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Handles OpenRouter model listing and interview Q&A extraction.
    /// </summary>
    public class OpenRouterService
    {
        private const string ModelsUrl = "https://openrouter.ai/api/v1/models";
        private const string CompletionsUrl = "https://openrouter.ai/api/v1/chat/completions";

        private readonly HttpClient _httpClient;

        public OpenRouterService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/live-transcript");
            _httpClient.DefaultRequestHeaders.Add("X-Title", "Live Transcript");
        }

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

        public Task<List<ExtractedQuestion>> ExtractQuestionAnswersAsync(
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
                CompletionsUrl,
                request => AddAuthHeader(request, apiKey),
                modelId,
                transcript,
                knownQuestions,
                jobDescription,
                resume,
                answerHistory,
                useJotNotes,
                "OpenRouter extraction/answer");
        }

        private static void AddAuthHeader(HttpRequestMessage request, string apiKey)
        {
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
        }
    }
}

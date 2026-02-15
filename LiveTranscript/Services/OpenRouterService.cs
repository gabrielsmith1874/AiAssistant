using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
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

        /// <summary>
        /// Sends the transcript to the chosen LLM to extract and answer
        /// interview questions as the candidate. Skips previously answered questions.
        /// </summary>
        public async Task<string> ExtractQuestionsAsync(
            string apiKey, string modelId,
            string transcript, string jobDescription, string resume)
        {
            var systemPrompt = BuildSystemPrompt(jobDescription, resume);
            var userPrompt = BuildUserPrompt(transcript);

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
            var answer = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "No response from model.";

            // Track this response for dedup on next call
            PreviouslyAnswered.Add(answer);

            return answer;
        }

        private string BuildSystemPrompt(string jobDescription, string resume)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an expert interview coach acting as a candidate.");
            sb.AppendLine();
            sb.AppendLine("TASK: Extract interview questions from the transcript and answer them.");
            sb.AppendLine();
            sb.AppendLine("RULES:");
            sb.AppendLine("1. EXTRACT: Questions asked by the INTERVIEWER. Ignore chat about the interview itself.");
            sb.AppendLine("   - Example: \"Did they ask about arrays?\" -> Ignore.");
            sb.AppendLine("   - Example: \"What is an array?\" -> EXTRACT.");
            sb.AppendLine("2. ANSWER: Be direct. No filler (\"Great question\", \"I believe\").");
            sb.AppendLine("   - KNOWLEDGE Qs: Define concept clearly first. Optional: 1 sentence experience.");
            sb.AppendLine("   - BEHAVIORAL Qs: Use experience from RESUME. Be specific (Situation, Action, Result).");
            sb.AppendLine("3. STYLE: Conversational, confident, professional. No corporate fluff.");
            sb.AppendLine();

            sb.AppendLine("OUTPUT JSON ARRAY:");
            sb.AppendLine("[ { \"q\": \"Question text?\", \"a\": \"Direct answer paragraph.\", \"k\": [\"Key point 1\", \"Key point 2\"] } ]");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();


            if (!string.IsNullOrWhiteSpace(jobDescription))
            {
                sb.AppendLine();
                sb.AppendLine("=== JOB ===");
                sb.AppendLine(jobDescription);
            }

            if (!string.IsNullOrWhiteSpace(resume))
            {
                sb.AppendLine();
                sb.AppendLine("=== RESUME ===");
                sb.AppendLine(resume);
            }

            return sb.ToString();
        }

        private string BuildUserPrompt(string transcript)
        {
            var sb = new StringBuilder();

            if (PreviouslyAnswered.Count > 0)
            {
                sb.AppendLine("=== ALREADY ANSWERED (DO NOT REPEAT THESE) ===");
                foreach (var prev in PreviouslyAnswered)
                    sb.AppendLine(prev);
                sb.AppendLine("=== END ALREADY ANSWERED ===");
                sb.AppendLine();
                sb.AppendLine("Only extract and answer NEW questions not covered above.");
                sb.AppendLine("If there are no new questions, respond with: \"No new questions detected.\"");
                sb.AppendLine();
            }

            sb.AppendLine("=== INTERVIEW TRANSCRIPT ===");
            sb.AppendLine(transcript);

            return sb.ToString();
        }

        public void ClearHistory() => PreviouslyAnswered.Clear();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using LiveTranscript.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Handles Anthropic Claude API for chat completions.
    /// Tracks previously answered questions to avoid duplicates.
    /// </summary>
    public class ClaudeService
    {
        private const string CompletionsUrl = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";

        private readonly HttpClient _httpClient;

        /// <summary>
        /// Accumulates Q&A pairs from previous extractions so the LLM
        /// skips already-answered questions on subsequent calls.
        /// </summary>
        public List<string> PreviouslyAnswered { get; } = new();

        public ClaudeService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        }

        /// <summary>
        /// Sends the transcript to Claude to extract and answer
        /// interview questions as the candidate. Skips previously answered questions.
        /// </summary>
        public async Task<string> ExtractQuestionsAsync(
            string apiKey, string modelId,
            string transcript, string jobDescription, string resume)
        {
            var systemPrompt = BuildSystemPrompt(jobDescription, resume);
            var userPrompt = BuildUserPrompt(transcript);

            var request = new ClaudeCompletionRequest
            {
                Model = modelId,
                MaxTokens = 4096,
                System = systemPrompt,
                Messages = new List<ClaudeMessage>
                {
                    new() { Role = "user", Content = userPrompt }
                }
            };

            var json = JsonConvert.SerializeObject(request);
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, CompletionsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Add("x-api-key", apiKey);

            var response = await _httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Claude API error ({response.StatusCode}): {responseText}");

            var result = JsonConvert.DeserializeObject<ClaudeCompletionResponse>(responseText);
            var answer = result?.Content?.FirstOrDefault()?.Text ?? "No response from model.";

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

    /// <summary>
    /// Claude API request model.
    /// </summary>
    public class ClaudeCompletionRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; } = string.Empty;

        [JsonProperty("max_tokens")]
        public int MaxTokens { get; set; } = 4096;

        [JsonProperty("system")]
        public string System { get; set; } = string.Empty;

        [JsonProperty("messages")]
        public List<ClaudeMessage> Messages { get; set; } = new();
    }

    public class ClaudeMessage
    {
        [JsonProperty("role")]
        public string Role { get; set; } = string.Empty;

        [JsonProperty("content")]
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>
    /// Claude API response model.
    /// </summary>
    public class ClaudeCompletionResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("role")]
        public string Role { get; set; } = string.Empty;

        [JsonProperty("content")]
        public List<ClaudeContentBlock>? Content { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; } = string.Empty;

        [JsonProperty("stop_reason")]
        public string StopReason { get; set; } = string.Empty;

        [JsonProperty("stop_sequence")]
        public string? StopSequence { get; set; }

        [JsonProperty("usage")]
        public ClaudeUsage? Usage { get; set; }
    }

    public class ClaudeContentBlock
    {
        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("text")]
        public string Text { get; set; } = string.Empty;
    }

    public class ClaudeUsage
    {
        [JsonProperty("input_tokens")]
        public int InputTokens { get; set; }

        [JsonProperty("output_tokens")]
        public int OutputTokens { get; set; }
    }
}
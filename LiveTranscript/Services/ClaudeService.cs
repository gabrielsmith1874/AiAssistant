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
            
            // Filter to only include 'text' blocks to avoid 'thinking' blocks being printed as the answer
            var answer = string.Join("\n", result?.Content?
                .Where(c => c.Type == "text")
                .Select(c => c.Text)
                .Where(t => !string.IsNullOrEmpty(t)) ?? Array.Empty<string>());

            if (string.IsNullOrWhiteSpace(answer))
                answer = "No response from model.";

            // Track this response for dedup on next call
            PreviouslyAnswered.Add(answer);

            return answer;
        }

        private string BuildSystemPrompt(string jobDescription, string resume)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an expert interview coach acting as the candidate.");
            sb.AppendLine();
            sb.AppendLine("TASK: Extract interview questions from the transcript and provide high-quality, targeted answers.");
            sb.AppendLine();
            sb.AppendLine("CORE RULES:");
            sb.AppendLine("1. EXTRACT: Only extract actual interview questions asked to the candidate. Ignore small talk or meta-commentary about the interview.");
            sb.AppendLine("2. BE THE CANDIDATE: Answer directly as the person being interviewed. Never say 'I would need your resume' or 'Focus on...'. If details are missing from the resume, provide a plausible, high-quality answer based on common industry standards for the role.");
            sb.AppendLine("3. KEYWORD OPTIMIZATION: These interviews are graded like a test. You MUST weave in industry-specific keywords and terminology relevant to the job and your resume to maximize the 'score'.");
            sb.AppendLine("4. NATURAL STAR FLOW: For behavioral questions, provide the context, your specific action, and the result, but do NOT use mnemonic labels (e.g., do not say 'Situation:', 'Task:', etc.). It must sound like a continuous, natural story.");
            sb.AppendLine("5. HUMAN STYLE & FLOW: Sound like a real person, not a robot. Use natural, conversational language that flows easily from left to right. Avoid corporate fluff and overly formal 'AI-speak'.");
            sb.AppendLine("6. NO METAPHORS OR DEVICES: Avoid metaphors, analogies, or artificial mnemonic devices that a human wouldn't naturally say on the spot. Be literal and direct.");
            sb.AppendLine("7. NO ACRONYMS: Do not use any acronyms (e.g., STAR, KPI, API, ROI, etc.). Always spell out the full terms (e.g., 'Key Performance Indicators' instead of 'KPIs').");
            sb.AppendLine("8. BE CONCISE: Keep answer paragraphs short (3-4 sentences max). Use clear, punchy key points.");
            sb.AppendLine("9. NO FILLER: Never start with 'That's a great question', 'Certainly', or 'Based on my resume'. Dive straight into the answer.");
            sb.AppendLine();

            sb.AppendLine("OUTPUT FORMAT (STRICT JSON):");
            sb.AppendLine("You MUST output ONLY a JSON array of objects. No preamble, no postamble. If no new questions are found, return an empty array [].");
            sb.AppendLine("[ { \"q\": \"The question?\", \"a\": \"Your direct answer paragraph.\", \"k\": [\"Key point 1\", \"Key point 2\"] } ]");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(jobDescription))
            {
                sb.AppendLine("=== TARGET JOB DESCRIPTION ===");
                sb.AppendLine(jobDescription);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(resume))
            {
                sb.AppendLine("=== YOUR RESUME (CANDIDATE DATA) ===");
                sb.AppendLine(resume);
                sb.AppendLine();
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
        public string? Text { get; set; }

        [JsonProperty("thinking")]
        public string? Thinking { get; set; }
    }

    public class ClaudeUsage
    {
        [JsonProperty("input_tokens")]
        public int InputTokens { get; set; }

        [JsonProperty("output_tokens")]
        public int OutputTokens { get; set; }
    }
}
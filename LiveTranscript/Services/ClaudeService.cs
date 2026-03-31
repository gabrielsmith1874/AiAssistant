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
        public async Task<List<string>> ExtractQuestionTextsOnlyAsync(
            string apiKey, string modelId,
            string transcript)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an expert interviewer. Extract only the question texts asked to the candidate from the transcript.");
            sb.AppendLine("OUTPUT FORMAT: Return ONLY a JSON array of strings. No preamble.");
            sb.AppendLine("Example: [ \"What is an array?\", \"Tell me about a time you failed.\" ]");

            var request = new ClaudeCompletionRequest
            {
                Model = modelId,
                MaxTokens = 1024,
                System = sb.ToString(),
                Thinking = new ClaudeThinkingConfig { Type = "disabled" },
                Messages = new List<ClaudeMessage>
                {
                    new() { Role = "user", Content = $"TRANSCRIPT:\n{transcript}" }
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

            var response = await _httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Extraction error: {responseText}");

            var result = JsonConvert.DeserializeObject<ClaudeCompletionResponse>(responseText);
            var text = result?.Content?.FirstOrDefault()?.Text ?? "[]";
            
            try 
            {
                return JsonConvert.DeserializeObject<List<string>>(text) ?? new List<string>();
            }
            catch 
            {
                // Fallback: search for array-like structure
                var match = Regex.Match(text, @"\[.*\]", RegexOptions.Singleline);
                if (match.Success)
                    return JsonConvert.DeserializeObject<List<string>>(match.Value) ?? new List<string>();
                return new List<string>();
            }
        }

        public async IAsyncEnumerable<string> StreamAnswerAsync(
            string apiKey, string modelId,
            string question, string transcript, string jobDescription, string resume)
        {
            var systemPrompt = BuildStreamingSystemPrompt(jobDescription, resume);
            var userPrompt = $"QUESTION: {question}\n\nCONTEXT TRANSCRIPT:\n{transcript}";

            var request = new ClaudeCompletionRequest
            {
                Model = modelId,
                MaxTokens = 2048,
                System = systemPrompt,
                Stream = true,
                Thinking = new ClaudeThinkingConfig { Type = "disabled" },
                Messages = new List<ClaudeMessage>
                {
                    new() { Role = "user", Content = userPrompt }
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

            using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                yield return $"[Error: {error}]";
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
                
                // Keep the last partial line
                leftover = lines[^1];

                // Process all complete lines
                for (int i = 0; i < lines.Length - 1; i++)
                {
                    var line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("data: "))
                    {
                        var data = line.Substring(6).Trim();
                        if (data == "[DONE]") yield break;

                        string? textToYield = null;
                        try
                        {
                            var delta = JsonConvert.DeserializeObject<JObject>(data);
                            var type = delta?["type"]?.ToString();

                            if (type == "content_block_delta")
                            {
                                var deltaNode = delta?["delta"];
                                var deltaType = deltaNode?["type"]?.ToString();
                                
                                if (deltaType == "text_delta")
                                {
                                    textToYield = deltaNode?["text"]?.ToString();
                                }
                            }
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(textToYield))
                            yield return textToYield;
                    }
                }
            }
        }

        private string BuildStreamingSystemPrompt(string jobDescription, string resume)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a Senior Backend Developer and Automated Test Engineer. Answer directly as the candidate.");
            sb.AppendLine();
            sb.AppendLine("CORE RULES:");
            sb.AppendLine("1. NO SPECIAL CHARACTERS: Use plain text only. No markdown bolding (**), no em-dashes. Use standard hyphens.");
            sb.AppendLine("2. NO PREAMBLE: Start immediately with the first technical point. No 'So,', 'Sure,', or 'I think...'.");
            sb.AppendLine("3. NO GRANULAR NUMBERS: Do not cite specific, small counts of files, failures, or components (e.g., avoid '17 files', '12 failures', '40% reduction'). Instead, use natural, general descriptions like 'several files', 'a handful of components', 'a significant portion of the codebase', or 'a noticeable improvement' to sound more realistic on the spot.");
            sb.AppendLine("4. DIRECT NARRATIVE FLOW: Connect points using logical, contextual transitions (e.g., 'Looking at the automation side...', 'Parallel to that development...'). Do NOT use reflective phrases like 'I realized' or 'I found valuable'. You are providing a direct technical answer based on expertise.");
            sb.AppendLine("4. EXPLAIN JARGON: Briefly define technical acronyms or system components (e.g., EJB, JSF, Data Adapter) when first mentioned.");
            sb.AppendLine("5. BACKEND/VENDOR CONTEXT: ADAM is a vendor-facing B2B app. Focus on backend stability and data integrity for vendor processing. Do not mention 1M+ users.");
            sb.AppendLine("6. BACKEND PERSONA: You are a technical contributor. You do not interact with users, stakeholders, or business clients. Your 'audience' is the dev and QA teams.");
            sb.AppendLine("7. TECHNICAL SUCCESS: Focus on 'Pipeline Reliability', 'Regression Coverage', and 'System Stability' instead of business outcomes.");
            sb.AppendLine("8. TECHNICAL STAR: Focus on implementation and debugging. Describe the technical Situation, Task, Action, and Result naturally without labels.");
            sb.AppendLine("9. KEYWORD OPTIMIZATION: Weave in technical keywords naturally.");
            sb.AppendLine("10. CONCISE: 3-4 sentences per answer.");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(jobDescription))
            {
                sb.AppendLine("=== TARGET JOB DESCRIPTION ===");
                sb.AppendLine(jobDescription);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(resume))
            {
                sb.AppendLine("=== YOUR RESUME (TECHNICAL DATA) ===");
                sb.AppendLine(resume);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string BuildSystemPrompt(string jobDescription, string resume)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a Senior Backend Developer and Automated Test Engineer.");
            sb.AppendLine();
            sb.AppendLine("TASK: Extract interview questions and provide direct, technical candidate answers.");
            sb.AppendLine();
            sb.AppendLine("CORE RULES:");
            sb.AppendLine("1. NO SPECIAL CHARACTERS: Use plain text only. No markdown bolding (**), no em-dashes.");
            sb.AppendLine("2. NO PREAMBLE: Start the answer immediately with technical content.");
            sb.AppendLine("3. NO GRANULAR NUMBERS: Avoid citing specific file counts or small metrics (e.g., '17 files', '12 bugs'). Use general terms like 'several', 'a group of', or 'the majority' to sound authentic.");
            sb.AppendLine("4. DIRECT NARRATIVE FLOW: Use logical connections (e.g., 'On the backend side...', 'Regarding test maintenance...'). No reflective 'I realized' or 'I found' phrases.");
            sb.AppendLine("4. EXPLAIN TECHNICAL TERMS: Briefly define technical acronyms (e.g., JSF, EJB, Data Adapters) when first mentioned.");
            sb.AppendLine("5. ADAM CONTEXT: ADAM is a vendor-facing B2B app. Focus on backend logic. No 1M+ user talk.");
            sb.AppendLine("6. TECHNICAL PERSONA: Pure backend contributor. No user/stakeholder interaction.");
            sb.AppendLine("7. NO BUSINESS LINGO: Skip 'ROI', 'UAT', 'Stakeholders'. Focus on 'Technical Debt' and 'Regression Coverage'.");
            sb.AppendLine("8. CONCISE: Keep it focused.");
            sb.AppendLine("9. NO INTERNAL THINKING: Generate the final response immediately.");
            sb.AppendLine();

            sb.AppendLine("OUTPUT FORMAT (STRICT JSON):");
            sb.AppendLine("You MUST output ONLY a JSON array of objects. No preamble, no postamble.");
            sb.AppendLine("[ { \"q\": \"The question?\", \"a\": \"Your direct technical answer paragraph.\", \"k\": [\"Key technical point 1\", \"Key technical point 2\"] } ]");
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

        [JsonProperty("stream")]
        public bool Stream { get; set; }

        [JsonProperty("thinking")]
        public ClaudeThinkingConfig? Thinking { get; set; }

        [JsonProperty("messages")]
        public List<ClaudeMessage> Messages { get; set; } = new();
    }

    public class ClaudeThinkingConfig
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "disabled";

        [JsonProperty("budget_tokens")]
        public int? BudgetTokens { get; set; }
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
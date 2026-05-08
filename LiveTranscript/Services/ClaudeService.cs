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
        public async Task<List<ExtractedQuestion>> ExtractQuestionTextsOnlyAsync(
            string apiKey, string modelId,
            string transcript,
            IEnumerable<string>? knownQuestions = null)
        {
            var systemPrompt = AiPromptTemplates.BuildQuestionExtractionSystemPrompt();
            var userPrompt = AiPromptTemplates.BuildQuestionExtractionUserPrompt(transcript, knownQuestions);

            var request = new ClaudeCompletionRequest
            {
                Model = modelId,
                MaxTokens = 1024,
                System = systemPrompt,
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

            var response = await _httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Extraction error: {responseText}");

            var result = JsonConvert.DeserializeObject<ClaudeCompletionResponse>(responseText);
            var text = result?.Content?.FirstOrDefault()?.Text ?? "[]";
             
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
                // Fallback: search for array-like structure
                var match = Regex.Match(text, @"\[.*\]", RegexOptions.Singleline);
                if (match.Success)
                {
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

                // Last fallback for old array format
                try
                {
                    var oldParsed = JsonConvert.DeserializeObject<List<string>>(text) ?? new List<string>();
                    return oldParsed
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => new ExtractedQuestion { Question = x.Trim() })
                        .ToList();
                }
                catch
                {
                    return new List<ExtractedQuestion>();
                }
            }
        }

        public async IAsyncEnumerable<string> StreamAnswerAsync(
            string apiKey, string modelId,
            string question, string transcript, string jobDescription, string resume,
            string? parentQuestion = null, string? parentAnswer = null)
        {
            var systemPrompt = AiPromptTemplates.BuildAnswerSystemPrompt(jobDescription, resume);
            var userPrompt = AiPromptTemplates.BuildAnswerUserPrompt(
                question, transcript, parentQuestion, parentAnswer);

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

        public void ClearHistory() => PreviouslyAnswered.Clear();
    }

    internal class ExtractedQuestionDto
    {
        [JsonProperty("q")]
        public string? Q { get; set; }

        [JsonProperty("f")]
        public bool? F { get; set; }

        [JsonProperty("p")]
        public string? P { get; set; }
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

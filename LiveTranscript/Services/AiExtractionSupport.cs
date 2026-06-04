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
    internal static class OpenAiCompatibleChatService
    {
        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public static async Task<List<ExtractedQuestion>> ExtractQuestionAnswersAsync(
            HttpClient httpClient,
            string completionsUrl,
            Action<HttpRequestMessage>? configureRequest,
            string modelId,
            string transcript,
            IEnumerable<string>? knownQuestions,
            string jobDescription,
            string resume,
            string? answerHistory,
            bool useJotNotes,
            string errorPrefix)
        {
            var request = new ChatCompletionRequest
            {
                Model = modelId,
                MaxTokens = 4096,
                Messages = new List<ChatMessage>
                {
                    new()
                    {
                        Role = "system",
                        Content = AiPromptTemplates.BuildQuestionAnswerExtractionSystemPrompt(
                            jobDescription, resume, useJotNotes)
                    },
                    new()
                    {
                        Role = "user",
                        Content = AiPromptTemplates.BuildQuestionAnswerExtractionUserPrompt(
                            transcript, knownQuestions, answerHistory)
                    }
                }
            };

            var responseText = await SendCompletionAsync(
                httpClient, completionsUrl, configureRequest, request, errorPrefix);
            var result = JsonConvert.DeserializeObject<ChatCompletionResponse>(responseText);
            var text = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "[]";

            return ExtractedQuestionParser.Parse(text);
        }

        private static async Task<string> SendCompletionAsync(
            HttpClient httpClient,
            string completionsUrl,
            Action<HttpRequestMessage>? configureRequest,
            ChatCompletionRequest request,
            string errorPrefix)
        {
            var json = JsonConvert.SerializeObject(request, JsonSettings);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, completionsUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            configureRequest?.Invoke(httpRequest);

            using var response = await httpClient.SendAsync(httpRequest);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"{errorPrefix} error ({response.StatusCode}): {responseText}");

            return responseText;
        }
    }

    internal static class ExtractedQuestionParser
    {
        public static List<ExtractedQuestion> Parse(string text)
        {
            try
            {
                return MapDtos(
                    JsonConvert.DeserializeObject<List<ExtractedQuestionDto>>(text) ?? new List<ExtractedQuestionDto>());
            }
            catch
            {
                var match = Regex.Match(text, @"\[.*\]", RegexOptions.Singleline);
                if (match.Success)
                {
                    try
                    {
                        return MapDtos(
                            JsonConvert.DeserializeObject<List<ExtractedQuestionDto>>(match.Value) ?? new List<ExtractedQuestionDto>());
                    }
                    catch
                    {
                        // Fall through to the legacy string-array parser below.
                    }
                }

                try
                {
                    return (JsonConvert.DeserializeObject<List<string>>(text) ?? new List<string>())
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

        private static List<ExtractedQuestion> MapDtos(IEnumerable<ExtractedQuestionDto> parsed)
        {
            return parsed
                .Where(x => !string.IsNullOrWhiteSpace(x.Q))
                .Select(x => new ExtractedQuestion
                {
                    Question = x.Q!.Trim(),
                    IsFollowUp = x.F ?? false,
                    ParentQuestion = (x.P ?? string.Empty).Trim(),
                    ParagraphAnswer = (x.A ?? string.Empty).Trim(),
                    KeyPoints = ReadJotNotes(x.K)
                })
                .ToList();
        }

        private static string ReadJotNotes(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return string.Empty;

            if (token.Type == JTokenType.Array)
            {
                return string.Join("\n", token
                    .Values<string>()
                    .Select(x => (x ?? string.Empty).Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.StartsWith("-", StringComparison.Ordinal) ? x : $"- {x}"));
            }

            return token.ToString().Trim();
        }

        private sealed class ExtractedQuestionDto
        {
            [JsonProperty("q")]
            public string? Q { get; set; }

            [JsonProperty("f")]
            public bool? F { get; set; }

            [JsonProperty("p")]
            public string? P { get; set; }

            [JsonProperty("a")]
            public string? A { get; set; }

            [JsonProperty("k")]
            public JToken? K { get; set; }
        }
    }
}

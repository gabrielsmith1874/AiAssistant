using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LiveTranscript.Models
{
    /// <summary>
    /// A single extracted interview question with its answer.
    /// Supports INotifyPropertyChanged for expand/collapse binding.
    /// </summary>
    public class QuestionAnswer : INotifyPropertyChanged
    {
        private bool _isExpanded = true;

        public int Number { get; set; }
        public string Question { get; set; } = string.Empty;
        public string ParagraphAnswer { get; set; } = string.Empty;
        public string KeyPoints { get; set; } = string.Empty;

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(nameof(IsExpanded)); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        /// <summary>
        /// Parses the LLM response into structured Q&A items.
        /// Supports both JSON (new) and Markdown (legacy) formats.
        /// </summary>
        public static List<QuestionAnswer> Parse(string response)
        {
            var items = new List<QuestionAnswer>();
            if (string.IsNullOrWhiteSpace(response)) return items;

            // 1. Try JSON parsing first (Preferred)
            try
            {
                // Find start and end of JSON array to handle potential markdown code blocks
                int startIndex = response.IndexOf('[');
                int endIndex = response.LastIndexOf(']');

                if (startIndex >= 0 && endIndex > startIndex)
                {
                    string json = response.Substring(startIndex, endIndex - startIndex + 1);
                    var dtos = JsonSerializer.Deserialize<List<QuestionDto>>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (dtos != null)
                    {
                        foreach (var dto in dtos)
                        {
                            var qa = new QuestionAnswer
                            {
                                Question = dto.Q ?? string.Empty,
                                ParagraphAnswer = dto.A ?? string.Empty
                            };

                            if (dto.K != null && dto.K.Count > 0)
                            {
                                qa.KeyPoints = string.Join("\n", dto.K.Select(k => $"• {k}"));
                            }

                            if (!string.IsNullOrEmpty(qa.Question))
                                items.Add(qa);
                        }
                        return items;
                    }
                }
            }
            catch
            {
                // JSON parsing failed, fall back to Regex
            }

            // 2. Legacy Regex Parsing
            // Split on "## Q:" markers
            var parts = Regex.Split(response, @"(?=##\s*Q:)", RegexOptions.Multiline);
            int num = 0;

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!trimmed.StartsWith("##")) continue;

                num++;
                var qa = new QuestionAnswer { Number = num };

                // Extract question from the header line
                var headerMatch = Regex.Match(trimmed, @"##\s*Q:\s*(.+?)(?:\r?\n|$)");
                if (headerMatch.Success)
                    qa.Question = headerMatch.Groups[1].Value.Trim();

                // Extract paragraph answer
                var answerMatch = Regex.Match(trimmed,
                    @"\*\*Answer:\*\*\s*\r?\n([\s\S]*?)(?=\*\*Key Points:\*\*|$)",
                    RegexOptions.Multiline);
                if (answerMatch.Success)
                    qa.ParagraphAnswer = answerMatch.Groups[1].Value.Trim();

                // Extract key points
                var keyPointsMatch = Regex.Match(trimmed,
                    @"\*\*Key Points:\*\*\s*\r?\n([\s\S]*?)(?=---|\z)",
                    RegexOptions.Multiline);
                if (keyPointsMatch.Success)
                {
                    var pointsRaw = keyPointsMatch.Groups[1].Value.Trim();
                    // Clean up: keep bullet points, remove markdown formatting
                    qa.KeyPoints = Regex.Replace(pointsRaw, @"^\s*-\s*", "• ", RegexOptions.Multiline).Trim();
                }

                if (!string.IsNullOrEmpty(qa.Question))
                    items.Add(qa);
            }

            return items;
        }

        private class QuestionDto
        {
            [JsonPropertyName("q")]
            public string? Q { get; set; }

            [JsonPropertyName("a")]
            public string? A { get; set; }

            [JsonPropertyName("k")]
            public List<string>? K { get; set; }
        }
    }
}

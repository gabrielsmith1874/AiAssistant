using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace LiveTranscript.Models
{
    public enum AudioSource
    {
        Microphone,
        SystemSpeaker,
        Both
    }

    public enum SessionState
    {
        Idle,
        Connecting,
        Recording,
        Error
    }

    // ── Transcript display model ──

    public class TranscriptEntry
    {
        public string Speaker { get; set; } = "Speaker ?";
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool IsFinal { get; set; }
        public int TurnOrder { get; set; }
    }

    // ── AssemblyAI WebSocket message models ──

    public class BaseMessage
    {
        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;
    }

    public class BeginEvent : BaseMessage
    {
        [JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        [JsonProperty("expires_at")]
        public long ExpiresAt { get; set; }
    }

    public class WordInfo
    {
        [JsonProperty("start")]
        public int Start { get; set; }

        [JsonProperty("end")]
        public int End { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; } = string.Empty;

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("word_is_final")]
        public bool WordIsFinal { get; set; }

        [JsonProperty("speaker")]
        public string? Speaker { get; set; }
    }

    public class TurnEvent : BaseMessage
    {
        [JsonProperty("turn_order")]
        public int TurnOrder { get; set; }

        [JsonProperty("turn_is_formatted")]
        public bool TurnIsFormatted { get; set; }

        [JsonProperty("end_of_turn")]
        public bool EndOfTurn { get; set; }

        [JsonProperty("transcript")]
        public string Transcript { get; set; } = string.Empty;

        [JsonProperty("end_of_turn_confidence")]
        public double EndOfTurnConfidence { get; set; }

        [JsonProperty("words")]
        public List<WordInfo> Words { get; set; } = new();

        [JsonProperty("speaker")]
        public string? Speaker { get; set; }
    }

    public class TerminationEvent : BaseMessage
    {
        [JsonProperty("audio_duration_seconds")]
        public double AudioDurationSeconds { get; set; }

        [JsonProperty("session_duration_seconds")]
        public double SessionDurationSeconds { get; set; }
    }

    public class TokenResponse
    {
        [JsonProperty("token")]
        public string Token { get; set; } = string.Empty;
    }
}

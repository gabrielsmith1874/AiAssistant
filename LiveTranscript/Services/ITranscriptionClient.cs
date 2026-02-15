using System;
using System.Threading.Tasks;
using LiveTranscript.Models;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Common interface for transcription providers (AssemblyAI, Deepgram, etc.)
    /// </summary>
    public interface ITranscriptionClient : IDisposable
    {
        event Action<string>? SessionStarted;
        event Action<TranscriptResult>? TranscriptReceived;
        event Action<string>? ErrorOccurred;
        event Action? SessionEnded;

        bool IsConnected { get; }

        Task ConnectAsync();
        Task SendAudioAsync(byte[] audioData);
        Task DisconnectAsync();
    }

    /// <summary>
    /// Normalized transcript result from any provider.
    /// </summary>
    public class TranscriptResult
    {
        public string Text { get; set; } = string.Empty;
        public bool IsFinal { get; set; }

        /// <summary>Speaker ID (e.g. 0, 1, 2). Null if not available.</summary>
        public int? SpeakerId { get; set; }

        /// <summary>Provider-specific turn/utterance order index.</summary>
        public int TurnOrder { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Streams audio to Deepgram's live transcription API with speaker diarization.
    /// WebSocket endpoint: wss://api.deepgram.com/v1/listen
    /// Auth: Token header with API key.
    /// Diarization: per-word speaker IDs in response.
    /// </summary>
    public class DeepgramClient : ITranscriptionClient
    {
        private const int SampleRate = 16000;

        private readonly string _apiKey;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _receiveCts;
        private bool _isConnected;
        private int _turnCounter;

        public event Action<string>? SessionStarted;
        public event Action<TranscriptResult>? TranscriptReceived;
        public event Action<string>? ErrorOccurred;
        public event Action? SessionEnded;

        public bool IsConnected => _isConnected;

        public DeepgramClient(string apiKey)
        {
            _apiKey = apiKey;
        }

        public async Task ConnectAsync()
        {
            if (_isConnected) return;

            try
            {
                _webSocket = new ClientWebSocket();
                _webSocket.Options.SetRequestHeader("Authorization", $"Token {_apiKey}");
                _receiveCts = new CancellationTokenSource();
                _turnCounter = 0;

                var url = $"wss://api.deepgram.com/v1/listen"
                    + $"?encoding=linear16&sample_rate={SampleRate}&channels=1"
                    + "&punctuate=true&smart_format=true&diarize=true"
                    + "&interim_results=true&endpointing=300"
                    + "&model=nova-2";

                await _webSocket.ConnectAsync(new Uri(url), CancellationToken.None);
                _isConnected = true;

                SessionStarted?.Invoke("deepgram-session");
                _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
            }
            catch (Exception ex)
            {
                _isConnected = false;
                ErrorOccurred?.Invoke($"Connection failed: {ex.Message}");
                throw;
            }
        }

        public async Task SendAudioAsync(byte[] audioData)
        {
            if (_webSocket?.State != WebSocketState.Open) return;

            try
            {
                await _webSocket.SendAsync(
                    new ArraySegment<byte>(audioData),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Send error: {ex.Message}");
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[16384];
            var messageBuilder = new StringBuilder();

            try
            {
                while (_webSocket?.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    messageBuilder.Clear();
                    WebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _isConnected = false;
                            SessionEnded?.Invoke();
                            return;
                        }

                        if (result.MessageType == WebSocketMessageType.Text)
                            messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                    while (!result.EndOfMessage);

                    if (messageBuilder.Length > 0)
                        ProcessMessage(messageBuilder.ToString());
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException)
            {
                _isConnected = false;
                SessionEnded?.Invoke();
            }
            catch (Exception ex)
            {
                _isConnected = false;
                ErrorOccurred?.Invoke($"Receive error: {ex.Message}");
            }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                var obj = JObject.Parse(json);
                var type = obj["type"]?.ToString();

                if (type != "Results") return;

                bool isFinal = obj["is_final"]?.Value<bool>() ?? false;
                bool speechFinal = obj["speech_final"]?.Value<bool>() ?? false;

                var channel = obj["channel"];
                var alternatives = channel?["alternatives"] as JArray;
                if (alternatives == null || alternatives.Count == 0) return;

                var best = alternatives[0];
                string transcript = best["transcript"]?.ToString()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(transcript)) return;

                // Extract dominant speaker from word-level diarization
                int? speakerId = GetDominantSpeaker(best["words"] as JArray);

                TranscriptReceived?.Invoke(new TranscriptResult
                {
                    Text = transcript,
                    IsFinal = isFinal,
                    SpeakerId = speakerId,
                    TurnOrder = _turnCounter
                });

                // Advance turn counter AFTER emitting, so the next segment gets a new key
                if (isFinal)
                    _turnCounter++;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Parse error: {ex.Message}");
            }
        }

        /// <summary>
        /// Determines the most frequent speaker ID from the words array.
        /// </summary>
        private int? GetDominantSpeaker(JArray? words)
        {
            if (words == null || words.Count == 0) return null;

            var counts = new Dictionary<int, int>();
            foreach (var word in words)
            {
                var speaker = word["speaker"];
                if (speaker != null)
                {
                    int id = speaker.Value<int>();
                    counts[id] = counts.GetValueOrDefault(id) + 1;
                }
            }

            if (counts.Count == 0) return null;
            return counts.OrderByDescending(kv => kv.Value).First().Key;
        }

        public async Task DisconnectAsync()
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    // Send close message (empty byte array signals end of audio)
                    var closeMsg = Encoding.UTF8.GetBytes("{\"type\": \"CloseStream\"}");
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(closeMsg),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None
                    );
                    await Task.Delay(500);
                }
                catch { }
            }

            _receiveCts?.Cancel();
            _isConnected = false;

            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
                }
                catch { }

                _webSocket.Dispose();
                _webSocket = null;
            }

            SessionEnded?.Invoke();
        }

        public void Dispose()
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _webSocket?.Dispose();
        }
    }
}

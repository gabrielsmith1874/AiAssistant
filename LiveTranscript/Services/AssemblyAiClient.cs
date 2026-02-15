using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveTranscript.Models;
using Newtonsoft.Json;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Manages the WebSocket connection to AssemblyAI's streaming API v3.
    /// Implements ITranscriptionClient for provider-agnostic usage.
    /// </summary>
    public class AssemblyAiClient : ITranscriptionClient
    {
        private const string ApiBaseUrl = "https://streaming.assemblyai.com/v3";
        private const string WsBaseUrl = "wss://streaming.assemblyai.com/v3/ws";
        private const int SampleRate = 16000;

        private readonly string _apiKey;
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _receiveCts;
        private readonly HttpClient _httpClient;
        private bool _isConnected;

        public event Action<string>? SessionStarted;
        public event Action<TranscriptResult>? TranscriptReceived;
        public event Action<string>? ErrorOccurred;
        public event Action? SessionEnded;

        public bool IsConnected => _isConnected;

        public AssemblyAiClient(string apiKey)
        {
            _apiKey = apiKey;
            _httpClient = new HttpClient();
        }

        private async Task<string> GetTemporaryTokenAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/token?expires_in_seconds=480");
            request.Headers.Add("Authorization", _apiKey);

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonConvert.DeserializeObject<TokenResponse>(json);
            return tokenResponse?.Token ?? throw new Exception("Failed to obtain streaming token");
        }

        public async Task ConnectAsync()
        {
            if (_isConnected) return;

            try
            {
                var token = await GetTemporaryTokenAsync();

                _webSocket = new ClientWebSocket();
                _receiveCts = new CancellationTokenSource();

                var wsUrl = $"{WsBaseUrl}?token={token}&sample_rate={SampleRate}&format_turns=true";
                await _webSocket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

                _isConnected = true;
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
            var buffer = new byte[8192];
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
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
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
                var baseMsg = JsonConvert.DeserializeObject<BaseMessage>(json);
                if (baseMsg == null) return;

                switch (baseMsg.Type)
                {
                    case "Begin":
                        var begin = JsonConvert.DeserializeObject<BeginEvent>(json);
                        if (begin != null) SessionStarted?.Invoke(begin.Id);
                        break;

                    case "Turn":
                        var turn = JsonConvert.DeserializeObject<TurnEvent>(json);
                        if (turn != null)
                        {
                            // Skip unformatted finals (formatted version follows)
                            if (turn.EndOfTurn && !turn.TurnIsFormatted)
                                return;

                            TranscriptReceived?.Invoke(new TranscriptResult
                            {
                                Text = turn.Transcript ?? string.Empty,
                                IsFinal = turn.EndOfTurn && turn.TurnIsFormatted,
                                SpeakerId = null, // AssemblyAI streaming doesn't provide speaker IDs
                                TurnOrder = turn.TurnOrder
                            });
                        }
                        break;

                    case "Termination":
                        _isConnected = false;
                        SessionEnded?.Invoke();
                        break;
                }
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Parse error: {ex.Message}");
            }
        }

        public async Task DisconnectAsync()
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var terminateMsg = Encoding.UTF8.GetBytes("{\"type\": \"Terminate\"}");
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(terminateMsg),
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
            _httpClient.Dispose();
        }
    }
}

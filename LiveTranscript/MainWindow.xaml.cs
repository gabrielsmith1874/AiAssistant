using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using System.Runtime.InteropServices;
using System.Windows.Interop;
using LiveTranscript.Models;
using LiveTranscript.Services;

namespace LiveTranscript
{
    public partial class MainWindow : Window
    {
        private readonly AudioCaptureService _audioService;
        private ITranscriptionClient? _micClient;
        private ITranscriptionClient? _speakerClient;
        private readonly TranscriptManager _transcriptManager;
        private readonly OpenRouterService _openRouterService;
        private readonly ClaudeService _claudeService;
        private SessionState _state = SessionState.Idle;
        private bool _isPinned = true;

        private readonly DispatcherTimer _pulseTimer;
        private bool _pulseOn;

        // Model browser state
        private List<OpenRouterModel> _allModels = new();
        private OpenRouterModel? _selectedModel;
        private AppSettings _settings = null!;
        private readonly List<QuestionAnswer> _qaItems = new();

        // Auto-extract state
        private bool _autoExtractEnabled;
        private int _lastExtractedEntryIndex;  // tracks how far we've extracted
        private bool _isExtracting;            // prevents overlapping calls
        private const int MinNewWords = 6;

        // Global Hotkey
        private const int HOTKEY_ID = 9000;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_OEM_2 = 0xBF; // /? key
        private const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainWindow()
        {
            InitializeComponent();

            _audioService = new AudioCaptureService();
            _transcriptManager = new TranscriptManager(Dispatcher);
            _openRouterService = new OpenRouterService();
            _claudeService = new ClaudeService();

            // Hook up hotkeys
            KeyDown += Window_KeyDown;

            TranscriptList.ItemsSource = _transcriptManager.Entries;
            _transcriptManager.Entries.CollectionChanged += (s, e) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    TranscriptScroller.ScrollToEnd();
                    EntryCount.Text = $"{_transcriptManager.Entries.Count} entries";
                    OnTranscriptChanged();
                }, DispatcherPriority.Background);
            };

            _audioService.MicDataAvailable += OnMicDataAvailable;
            _audioService.SpeakerDataAvailable += OnSpeakerDataAvailable;
            _audioService.Error += msg => UpdateStatus($"Audio: {msg}", isError: true);

            _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _pulseTimer.Tick += (s, e) =>
            {
                _pulseOn = !_pulseOn;
                RecordingDot.Fill = _pulseOn
                    ? FindResource("AccentRedBrush") as SolidColorBrush
                    : new SolidColorBrush(Color.FromArgb(80, 255, 107, 157));
            };



            LoadSettings();

            // Register global hotkey on load
            Loaded += (s, e) => RegisterGlobalHotkey();

            // Restore window state
            if (_settings.WindowTop >= 0 && _settings.WindowLeft >= 0)
            {
                Top = _settings.WindowTop;
                Left = _settings.WindowLeft;
            }
            if (_settings.WindowWidth > 100) Width = _settings.WindowWidth;
            if (_settings.WindowHeight > 100) Height = _settings.WindowHeight;

            // Load models in background
            _ = LoadModelsAsync();
            
            // Save state on close

            Closing += (s, e) =>
            {
                UnregisterGlobalHotkey();
                _settings.WindowTop = Top;
                _settings.WindowLeft = Left;
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
                _settings.Save();
            };
        }

        private void RegisterGlobalHotkey()
        {
            var helper = new WindowInteropHelper(this);
            var source = HwndSource.FromHwnd(helper.Handle);
            source.AddHook(HwndHook);
            RegisterHotKey(helper.Handle, HOTKEY_ID, MOD_CONTROL, VK_OEM_2);
        }

        private void UnregisterGlobalHotkey()
        {
            var helper = new WindowInteropHelper(this);
            UnregisterHotKey(helper.Handle, HOTKEY_ID);
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                OnGlobalHotkey();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void OnGlobalHotkey()
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                Activate();
            }
            else
            {
                WindowState = WindowState.Minimized;
            }
        }

        // ── Helpers ──

        private AudioSource GetSelectedSource() => SourceSelector.SelectedIndex switch
        {
            0 => AudioSource.Microphone,
            1 => AudioSource.SystemSpeaker,
            _ => AudioSource.Both
        };

        private bool IsDeepgram => ProviderSelector.SelectedIndex == 1;
        private bool IsClaude => _settings.AiProvider == "Claude";

        private string GetAiApiKey()
        {
            return IsClaude ? _settings.ClaudeApiKey : _settings.OpenRouterApiKey;
        }

        private string GetAiModelId()
        {
            if (IsClaude)
            {
                // Use Claude 4.5 Haiku as the default model
                return "claude-haiku-4-5";
            }
            return _selectedModel?.Id ?? string.Empty;
        }

        private string GetApiKey()
        {
            return IsDeepgram ? _settings.DeepgramApiKey : _settings.AssemblyAiApiKey;
        }

        private ITranscriptionClient CreateClient(string apiKey)
        {
            return IsDeepgram
                ? new DeepgramClient(apiKey)
                : new AssemblyAiClient(apiKey);
        }



        private void LoadSettings()
        {
            _settings = AppSettings.Load();
            SettingsApiKey.Text = _settings.OpenRouterApiKey;
            SettingsClaudeApiKey.Text = _settings.ClaudeApiKey;
            SettingsAssemblyKey.Text = _settings.AssemblyAiApiKey;
            SettingsDeepgramKey.Text = _settings.DeepgramApiKey;
            SettingsJobDesc.Text = _settings.JobDescription;
            SettingsResume.Text = _settings.Resume;

            // Load AI provider selection
            SettingsAiProvider.SelectedIndex = _settings.AiProvider == "Claude" ? 1 : 0;
            UpdateModelBrowserVisibility();

            if (string.IsNullOrEmpty(_settings.AssemblyAiApiKey) && string.IsNullOrEmpty(_settings.DeepgramApiKey))
                UpdateStatus("⚠ Set API key(s) in Settings", isError: true);
            else
                UpdateStatus("Ready — Press Start to begin");
        }

        private void UpdateModelBrowserVisibility()
        {
            var isClaude = _settings.AiProvider == "Claude";
            
            // Toggle visibility of model browser vs Claude indicator
            ModelFilterBar.Visibility = isClaude ? Visibility.Collapsed : Visibility.Visible;
            ModelListView.Visibility = isClaude ? Visibility.Collapsed : Visibility.Visible;
            ClaudeModelIndicator.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;

            // Enable extract buttons when Claude is selected (no model selection needed)
            ExtractButton.IsEnabled = isClaude || _selectedModel != null;
            AutoExtractButton.IsEnabled = isClaude || _selectedModel != null;
        }



        // ── Model Browser ──

        private async Task LoadModelsAsync()
        {
            try
            {
                UpdateStatus("Loading models…");
                _allModels = await _openRouterService.FetchModelsAsync();
                Dispatcher.Invoke(() =>
                {
                    ApplyModelFilters();
                    UpdateStatus($"Ready — {_allModels.Count} models loaded");
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => UpdateStatus($"Model load failed: {ex.Message}", isError: true));
            }
        }

        private void ApplyModelFilters()
        {
            var query = ModelSearchBox.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            bool freeOnly = FreeOnlyCheck.IsChecked == true;

            var filtered = _allModels.AsEnumerable();

            if (freeOnly)
                filtered = filtered.Where(m => m.IsFree);

            if (!string.IsNullOrEmpty(query))
                filtered = filtered.Where(m =>
                    m.Name.ToLowerInvariant().Contains(query) ||
                    m.Id.ToLowerInvariant().Contains(query));

            // Sort: free first, then by context length descending
            var sorted = filtered
                .OrderByDescending(m => m.IsFree)
                .ThenByDescending(m => m.ContextLength)
                .ToList();

            ModelListView.ItemsSource = sorted;

            // Restore selection if saved
            if (!string.IsNullOrEmpty(_settings.SelectedModelId))
            {
                var saved = sorted.FirstOrDefault(m => m.Id == _settings.SelectedModelId);
                if (saved != null)
                {
                    ModelListView.SelectedItem = saved;
                    ModelListView.ScrollIntoView(saved);
                }
            }
        }

        // ── UI Event Handlers ──

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            _isPinned = !_isPinned;
            Topmost = _isPinned;
            PinButton.Content = _isPinned ? "📌" : "📍";
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _ = StopSessionAsync();
            _audioService.Dispose();
            _micClient?.Dispose();
            _speakerClient?.Dispose();
            Close();
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => _transcriptManager.Clear();

        private async void StartStop_Click(object sender, RoutedEventArgs e)
        {
            if (_state == SessionState.Recording || _state == SessionState.Connecting)
                await StopSessionAsync();
            else
                await StartSessionAsync();
        }

        // Model browser events
        private void ModelSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyModelFilters();
        private void FreeOnly_Changed(object sender, RoutedEventArgs e) => ApplyModelFilters();
        private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await LoadModelsAsync();

        private void ModelList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _selectedModel = ModelListView.SelectedItem as OpenRouterModel;
            ExtractButton.IsEnabled = _selectedModel != null;
            AutoExtractButton.IsEnabled = _selectedModel != null;

            if (_selectedModel != null)
            {
                _settings.SelectedModelId = _selectedModel.Id;
                _settings.Save();
            }
        }

        private async void Extract_Click(object sender, RoutedEventArgs e)
        {
            if (!IsClaude && _selectedModel == null) return;

            var apiKey = GetAiApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                UpdateStatus($"⚠ Set {(IsClaude ? "Claude" : "OpenRouter")} API key in Settings", isError: true);
                return;
            }

            var transcript = BuildTranscriptText();
            if (string.IsNullOrEmpty(transcript))
            {
                UpdateStatus("⚠ No transcript to analyze", isError: true);
                return;
            }

            var modelId = GetAiModelId();
            var modelName = IsClaude ? "Claude 4.5 Haiku" : _selectedModel?.Name;

            try
            {
                ExtractButton.IsEnabled = false;
                ExtractButton.Content = "⏳  Analyzing…";
                AiStatusText.Text = $"Extracting questions with {modelName}…";
                UpdateStatus($"Extracting questions with {modelName}…");

                List<string> questionTexts;
                if (IsClaude)
                {
                    questionTexts = await _claudeService.ExtractQuestionTextsOnlyAsync(apiKey, modelId, transcript);
                }
                else
                {
                    // Fallback for OpenRouter (non-streaming for now to keep it simple)
                    var rawResult = await _openRouterService.ExtractQuestionsAsync(
                        apiKey, modelId,
                        transcript, _settings.JobDescription, _settings.Resume);
                    var openRouterItems = QuestionAnswer.Parse(rawResult);
                    
                    int startNum = _qaItems.Count;
                    foreach (var item in openRouterItems)
                    {
                        item.Number = ++startNum;
                        _qaItems.Add(item);
                    }
                    QaList.ItemsSource = null;
                    QaList.ItemsSource = _qaItems;
                    AiStatusText.Text = $"{_qaItems.Count} questions extracted";
                    return;
                }

                if (questionTexts.Count == 0)
                {
                    AiStatusText.Text = "No new questions detected.";
                }
                else
                {
                    int startNum = _qaItems.Count;
                    var newQas = new List<QuestionAnswer>();

                    foreach (var qText in questionTexts)
                    {
                        // Check if we already have this exact question to avoid duplicates
                        if (_qaItems.Any(existing => existing.Question.Equals(qText, StringComparison.OrdinalIgnoreCase)))
                            continue;

                        var qa = new QuestionAnswer
                        {
                            Number = ++startNum,
                            Question = qText,
                            ParagraphAnswer = "⏳ Thinking...",
                            IsExpanded = true
                        };
                        _qaItems.Add(qa);
                        newQas.Add(qa);
                    }

                    QaList.ItemsSource = null;
                    QaList.ItemsSource = _qaItems;
                    AiStatusText.Text = $"{_qaItems.Count} questions found. Streaming answers...";

                    // Start streaming answers in background for each NEW question
                    foreach (var qa in newQas)
                    {
                        _ = ProcessStreamingAnswer(qa, apiKey, modelId, transcript);
                    }
                }

                UpdateStatus("✅ Questions extracted");
            }
            catch (Exception ex)
            {
                UpdateStatus($"AI error: {ex.Message}", isError: true);
                AiStatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                ExtractButton.Content = "🧠  Extract Questions";
                ExtractButton.IsEnabled = true;
            }
        }

        private async Task ProcessStreamingAnswer(QuestionAnswer qa, string apiKey, string modelId, string transcript)
        {
            try
            {
                var firstChunk = true;
                var fullAnswer = new StringBuilder();

                await foreach (var chunk in _claudeService.StreamAnswerAsync(
                    apiKey, modelId, qa.Question, transcript, _settings.JobDescription, _settings.Resume))
                {
                    if (firstChunk)
                    {
                        qa.ParagraphAnswer = "";
                        firstChunk = false;
                    }

                    qa.ParagraphAnswer += chunk;
                    fullAnswer.Append(chunk);
                    
                    // Auto-scroll the QA list if it's the last item
                    if (qa == _qaItems.LastOrDefault())
                    {
                        QaScroller.ScrollToEnd();
                    }
                }

                // Tracking answered questions to avoid duplicates in future extractions
                _claudeService.PreviouslyAnswered.Add($"Q: {qa.Question}\nA: {fullAnswer}");
                
                // Final UI update
                AiStatusText.Text = $"{_qaItems.Count} questions extracted";
            }
            catch (Exception ex)
            {
                qa.ParagraphAnswer = $"[Error streaming answer: {ex.Message}]";
            }
        }

        private void ClearAiHistory_Click(object sender, RoutedEventArgs e)
        {
            _openRouterService.ClearHistory();
            _claudeService.ClearHistory();
            _qaItems.Clear();
            _lastExtractedEntryIndex = 0;
            QaList.ItemsSource = null;
            AiStatusText.Text = IsClaude ? "Click Extract Questions" : "Select a model and click Extract Questions";
            UpdateStatus("AI history cleared");
        }

        // ── Auto-Extract ──

        private void AutoExtract_Click(object sender, RoutedEventArgs e)
        {
            _autoExtractEnabled = !_autoExtractEnabled;

            if (_autoExtractEnabled)
            {
                AutoExtractButton.Content = "⚡ Auto ●";
                AutoExtractButton.Foreground = FindResource("AccentGreenBrush") as SolidColorBrush
                    ?? new SolidColorBrush(Color.FromRgb(0, 210, 130));
                _lastExtractedEntryIndex = _transcriptManager.Entries.Count;
                UpdateStatus($"Auto-extract ON — immediate trigger ({MinNewWords} words)");
            }
            else
            {
                AutoExtractButton.Content = "⚡ Auto";
                AutoExtractButton.Foreground = FindResource("TextSecondaryBrush") as SolidColorBrush
                    ?? new SolidColorBrush(Colors.Gray);
                UpdateStatus("Auto-extract OFF");
            }
        }

        private async void OnTranscriptChanged()
        {
            if (!_autoExtractEnabled || _isExtracting) return;

            // Check if we have enough new content to trigger
            var entries = _transcriptManager.Entries;
            if (entries.Count <= _lastExtractedEntryIndex) return;

            // Quickly estimate word count of new finalized entries
            // (We only check IsFinal to avoid triggering on unstable partials)
            int newWordCount = 0;
            for (int i = _lastExtractedEntryIndex; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.IsFinal && !string.IsNullOrWhiteSpace(e.Text))
                {
                    newWordCount += e.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                }
            }

            if (newWordCount >= MinNewWords)
            {
                await RunAutoExtractAsync();
            }
        }

        /// <summary>
        /// Builds only the NEW transcript since last extraction and sends it.
        /// Only fires if there are enough new words to be worth an API call.
        /// </summary>
        private async Task RunAutoExtractAsync()
        {
            if (_isExtracting || (!IsClaude && _selectedModel == null)) return;

            var apiKey = GetAiApiKey();
            if (string.IsNullOrEmpty(apiKey)) return;

            // Build incremental transcript (only new entries)
            var entries = _transcriptManager.Entries;
            var newEntries = entries.Skip(_lastExtractedEntryIndex)
                .Where(e => e.IsFinal)
                .ToList();

            var newText = string.Join("\n",
                newEntries.Select(e => $"[{e.Speaker}] {e.Text}"));

            // Check minimum word threshold
            int wordCount = newText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (wordCount < MinNewWords) return;

            _isExtracting = true;
            try
            {
                var modelId = GetAiModelId();
                var modelName = IsClaude ? "Claude 4.5 Haiku" : _selectedModel?.Name;
                AiStatusText.Text = $"Auto-extracting ({wordCount} new words)…";

                if (IsClaude)
                {
                    var questionTexts = await _claudeService.ExtractQuestionTextsOnlyAsync(apiKey, modelId, newText);
                    _lastExtractedEntryIndex = entries.Count;

                    if (questionTexts.Count > 0)
                    {
                        int startNum = _qaItems.Count;
                        var newQas = new List<QuestionAnswer>();

                        foreach (var qText in questionTexts)
                        {
                            if (_qaItems.Any(existing => existing.Question.Equals(qText, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            var qa = new QuestionAnswer
                            {
                                Number = ++startNum,
                                Question = qText,
                                ParagraphAnswer = "⏳ Thinking...",
                                IsExpanded = true
                            };
                            _qaItems.Add(qa);
                            newQas.Add(qa);
                        }

                        if (newQas.Count > 0)
                        {
                            QaList.ItemsSource = null;
                            QaList.ItemsSource = _qaItems;
                            AiStatusText.Text = $"{_qaItems.Count} questions found (auto). Streaming...";

                            foreach (var qa in newQas)
                            {
                                _ = ProcessStreamingAnswer(qa, apiKey, modelId, BuildTranscriptText());
                            }
                        }
                    }
                }
                else
                {
                    var rawResult = await _openRouterService.ExtractQuestionsAsync(
                        apiKey, modelId,
                        newText, _settings.JobDescription, _settings.Resume);

                    _lastExtractedEntryIndex = entries.Count;

                    var newItems = QuestionAnswer.Parse(rawResult);
                    if (newItems.Count > 0)
                    {
                        int startNum = _qaItems.Count;
                        foreach (var item in newItems)
                        {
                            item.Number = ++startNum;
                            _qaItems.Add(item);
                        }
                        QaList.ItemsSource = null;
                        QaList.ItemsSource = _qaItems;
                        AiStatusText.Text = $"{_qaItems.Count} questions extracted (auto)";
                    }
                }
            }
            catch (Exception ex)
            {
                AiStatusText.Text = $"Auto-extract error: {ex.Message}";
            }
            finally
            {
                _isExtracting = false;
            }
        }

        private void ToggleAnswer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is QuestionAnswer qa)
                qa.IsExpanded = !qa.IsExpanded;
        }

        private bool _allExpanded;
        private void ToggleAll_Click(object sender, RoutedEventArgs e)
        {
            _allExpanded = !_allExpanded;
            foreach (var qa in _qaItems)
                qa.IsExpanded = _allExpanded;
            ToggleAllButton.Content = _allExpanded ? "👁 Hide All" : "👁 Show All";
        }

        private string BuildTranscriptText()
        {
            var sb = new StringBuilder();
            foreach (var entry in _transcriptManager.Entries)
            {
                if (entry.IsFinal)
                    sb.AppendLine($"[{entry.Speaker}] {entry.Text}");
            }
            return sb.ToString().Trim();
        }

        private void CopyTranscriptAll_Click(object sender, RoutedEventArgs e)
        {
            var text = BuildTranscriptText();
            if (string.IsNullOrWhiteSpace(text))
            {
                UpdateStatus("⚠ No transcript to copy", isError: true);
                return;
            }

            Clipboard.SetText(text);
            UpdateStatus("📋 Transcript copied");
        }

        private void CopyQaAll_Click(object sender, RoutedEventArgs e)
        {
            if (_qaItems.Count == 0)
            {
                UpdateStatus("⚠ No questions to copy", isError: true);
                return;
            }

            var sb = new StringBuilder();
            foreach (var qa in _qaItems)
            {
                sb.AppendLine($"Q{qa.Number}: {qa.Question}".Trim());
                if (!string.IsNullOrWhiteSpace(qa.ParagraphAnswer))
                {
                    sb.AppendLine();
                    sb.AppendLine(qa.ParagraphAnswer.Trim());
                }

                if (!string.IsNullOrWhiteSpace(qa.KeyPoints))
                {
                    sb.AppendLine();
                    sb.AppendLine(qa.KeyPoints.Trim());
                }

                sb.AppendLine();
                sb.AppendLine(new string('-', 32));
                sb.AppendLine();
            }

            var text = sb.ToString().Trim();
            Clipboard.SetText(text);
            UpdateStatus("📋 Q&A copied");
        }

        // Settings events
        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = SettingsPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.OpenRouterApiKey = SettingsApiKey.Text.Trim();
            _settings.ClaudeApiKey = SettingsClaudeApiKey.Text.Trim();
            _settings.AssemblyAiApiKey = SettingsAssemblyKey.Text.Trim();
            _settings.DeepgramApiKey = SettingsDeepgramKey.Text.Trim();
            _settings.JobDescription = SettingsJobDesc.Text.Trim();
            _settings.Resume = SettingsResume.Text.Trim();
            _settings.AiProvider = SettingsAiProvider.SelectedIndex == 1 ? "Claude" : "OpenRouter";
            _settings.Save();
            UpdateModelBrowserVisibility();
            UpdateStatus("Settings saved ✓");
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        // ── Session Lifecycle ──

        private async Task StartSessionAsync()
        {
            var apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey) || apiKey == "your_api_key_here")
            {
                string keyName = IsDeepgram ? "DEEPGRAM_API_KEY" : "ASSEMBLYAI_API_KEY";
                UpdateStatus($"⚠ Set {keyName} in .env", isError: true);
                return;
            }

            var source = GetSelectedSource();
            SetState(SessionState.Connecting);

            try
            {
                _micClient?.Dispose();
                _speakerClient?.Dispose();
                _micClient = null;
                _speakerClient = null;

                var connectTasks = new List<Task>();

                if (source == AudioSource.Microphone || source == AudioSource.Both)
                {
                    _micClient = CreateClient(apiKey);
                    _micClient.SessionStarted += id =>
                        Dispatcher.Invoke(() => UpdateStatus("🔴 Recording"));
                    _micClient.TranscriptReceived += result =>
                        _transcriptManager.ProcessResult(result, "mic");
                    _micClient.ErrorOccurred += msg =>
                        Dispatcher.Invoke(() => UpdateStatus($"Mic: {msg}", isError: true));
                    _micClient.SessionEnded += () => Dispatcher.Invoke(CheckSessionsAlive);
                    connectTasks.Add(_micClient.ConnectAsync());
                }

                if (source == AudioSource.SystemSpeaker || source == AudioSource.Both)
                {
                    _speakerClient = CreateClient(apiKey);
                    _speakerClient.SessionStarted += id =>
                        Dispatcher.Invoke(() => UpdateStatus("🔴 Recording"));
                    _speakerClient.TranscriptReceived += result =>
                        _transcriptManager.ProcessResult(result, "speaker");
                    _speakerClient.ErrorOccurred += msg =>
                        Dispatcher.Invoke(() => UpdateStatus($"Speaker: {msg}", isError: true));
                    _speakerClient.SessionEnded += () => Dispatcher.Invoke(CheckSessionsAlive);
                    connectTasks.Add(_speakerClient.ConnectAsync());
                }

                await Task.WhenAll(connectTasks);
                _audioService.Start(source);
                SetState(SessionState.Recording);
            }
            catch (Exception ex)
            {
                SetState(SessionState.Error);
                UpdateStatus($"Failed: {ex.Message}", isError: true);
            }
        }

        private void CheckSessionsAlive()
        {
            if (_state == SessionState.Idle) return;
            if (_micClient?.IsConnected != true && _speakerClient?.IsConnected != true)
                SetState(SessionState.Idle);
        }

        private async Task StopSessionAsync()
        {
            _audioService.Stop();

            var tasks = new List<Task>();
            if (_micClient != null)
                tasks.Add(Task.Run(async () => { try { await _micClient.DisconnectAsync(); } catch { } }));
            if (_speakerClient != null)
                tasks.Add(Task.Run(async () => { try { await _speakerClient.DisconnectAsync(); } catch { } }));

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);

            SetState(SessionState.Idle);
        }

        // ── Audio routing ──

        private async void OnMicDataAvailable(byte[] data)
        {
            if (_micClient?.IsConnected == true)
            {
                try { await _micClient.SendAudioAsync(data); }
                catch { }
            }
        }

        private async void OnSpeakerDataAvailable(byte[] data)
        {
            if (_speakerClient?.IsConnected == true)
            {
                try { await _speakerClient.SendAudioAsync(data); }
                catch { }
            }
        }

        // ── UI State ──

        private void SetState(SessionState state)
        {
            _state = state;
            Dispatcher.Invoke(() =>
            {
                bool idle = state == SessionState.Idle || state == SessionState.Error;
                SourceSelector.IsEnabled = idle;
                ProviderSelector.IsEnabled = idle;

                switch (state)
                {
                    case SessionState.Idle:
                        StartStopButton.Content = "▶  Start";
                        RecordingDot.Fill = FindResource("TextSecondaryBrush") as SolidColorBrush;
                        _pulseTimer.Stop();
                        UpdateStatus("Ready — Press Start to begin");
                        break;
                    case SessionState.Connecting:
                        StartStopButton.Content = "⏳  Connecting…";
                        RecordingDot.Fill = new SolidColorBrush(Color.FromRgb(255, 195, 0));
                        UpdateStatus("Connecting…");
                        break;
                    case SessionState.Recording:
                        StartStopButton.Content = "⏹  Stop";
                        _pulseTimer.Start();
                        break;
                    case SessionState.Error:
                        StartStopButton.Content = "▶  Start";
                        RecordingDot.Fill = FindResource("AccentRedBrush") as SolidColorBrush;
                        _pulseTimer.Stop();
                        break;
                }
            });
        }

        private void UpdateStatus(string text, bool isError = false)
        {
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = text;
                StatusText.Foreground = isError
                    ? FindResource("AccentRedBrush") as SolidColorBrush
                    : FindResource("TextSecondaryBrush") as SolidColorBrush;
            });
        }


        // ── Hotkeys ──

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Toggle Overlay (Ctrl + /) - Handled even if focused on text box
            if (e.Key == Key.OemQuestion && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                else
                    WindowState = WindowState.Minimized;
                return;
            }

            // Ignore if user is typing in a text box
            if (e.OriginalSource is System.Windows.Controls.TextBox ||
                e.OriginalSource is System.Windows.Controls.PasswordBox)
            {
                return;
            }

            switch (e.Key)
            {
                case Key.Q: // Start/Stop
                    StartStop_Click(this, new RoutedEventArgs());
                    break;

                case Key.W: // Extract
                    if (ExtractButton.IsEnabled)
                        Extract_Click(this, new RoutedEventArgs());
                    break;

                case Key.E: // Clear Transcript
                    if (TranscriptList.Items.Count > 0)
                        Clear_Click(this, new RoutedEventArgs());
                    break;

                case Key.R: // Clear AI
                    ClearAiHistory_Click(this, new RoutedEventArgs());
                    break;

                case Key.T: // Show/Hide All
                    ToggleAll_Click(this, new RoutedEventArgs());
                    break;

                case Key.Y: // Auto Toggle
                    if (AutoExtractButton.IsEnabled)
                        AutoExtract_Click(this, new RoutedEventArgs());
                    break;
            }
        }
    }
}
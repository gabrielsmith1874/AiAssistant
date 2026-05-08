using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Collections.ObjectModel;
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
        private readonly LmStudioService _lmStudioService;
        private SessionState _state = SessionState.Idle;
        private bool _isPinned = true;
        private bool _isHudMode;
        private Rect _windowedBounds = Rect.Empty;

        private readonly DispatcherTimer _pulseTimer;
        private bool _pulseOn;

        // Model browser state
        private List<OpenRouterModel> _allModels = new();
        private OpenRouterModel? _selectedModel;
        private AppSettings _settings = null!;
        private readonly ObservableCollection<QuestionAnswer> _qaItems = new();

        // Auto-extract state
        private bool _autoExtractEnabled;
        private int _lastExtractedEntryIndex;  // tracks how far we've extracted
        private bool _isExtracting;            // prevents overlapping calls
        private const int MinNewWords = 6;
        private const int AutoStreamDebounceMs = 1400;
        private const int AutoStreamMaxWaitMs = 7000;
        private const int AutoStreamOverlapEntries = 4;
        private const int MinManualIncrementalWords = 8;
        private const int ExtractionContextMaxChars = 9000;
        private const int AnswerContextMaxChars = 14000;
        private const int DeferredStreamStartMs = 1200;
        private readonly HashSet<string> _warmedLmStudioModels = new();
        private readonly DispatcherTimer _autoStreamDebounceTimer;
        private DateTime _autoStreamPendingSince = DateTime.MinValue;
        private Border? _activeHudModule;
        private FrameworkElement? _activeHudHeader;
        private Point _hudDragStartPoint;
        private double _hudDragStartLeft;
        private double _hudDragStartTop;
        private Border? _activeHudResizeModule;
        private FrameworkElement? _activeHudResizeHandle;
        private Point _hudResizeStartPoint;
        private double _hudResizeStartWidth;
        private double _hudResizeStartHeight;
        private bool _hudModulesInitialized;

        // Global Hotkey
        private const int HOTKEY_ID = 9000;
        private const uint MOD_CONTROL = 0x0002;
        private const uint VK_OEM_2 = 0xBF; // /? key
        private const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
        private const uint WDA_MONITOR = 0x00000001;
        private const uint WDA_NONE = 0x00000000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private enum OverlayMode
        {
            Hidden,
            Windowed,
            Hud
        }

        public MainWindow()
        {
            InitializeComponent();

            _audioService = new AudioCaptureService();
            _transcriptManager = new TranscriptManager(Dispatcher);
            _openRouterService = new OpenRouterService();
            _claudeService = new ClaudeService();
            _lmStudioService = new LmStudioService();

            // Hook up hotkeys
            KeyDown += Window_KeyDown;

            TranscriptList.ItemsSource = _transcriptManager.Entries;
            HudTranscriptList.ItemsSource = _transcriptManager.Entries;
            QaList.ItemsSource = _qaItems;
            HudQaList.ItemsSource = _qaItems;
            _transcriptManager.Entries.CollectionChanged += (s, e) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    TranscriptScroller.ScrollToEnd();
                    if (_isHudMode)
                        HudTranscriptScroller.ScrollToEnd();
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

            _autoStreamDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AutoStreamDebounceMs) };
            _autoStreamDebounceTimer.Tick += async (s, e) =>
            {
                _autoStreamDebounceTimer.Stop();
                await RunAutoExtractAsync();
            };



            LoadSettings();

            // Register global hotkey on load
            Loaded += (s, e) =>
            {
                RegisterGlobalHotkey();
                ExcludeFromCapture();
            };

            // Transparent windows can leave visual artifacts when minimized, so hide instead.
            StateChanged += (s, e) =>
            {
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                    Hide();
                }
            };

            // Restore window state
            if (_settings.WindowTop >= 0 && _settings.WindowLeft >= 0)
            {
                Top = _settings.WindowTop;
                Left = _settings.WindowLeft;
            }
            if (_settings.WindowWidth > 100) Width = _settings.WindowWidth;
            if (_settings.WindowHeight > 100) Height = _settings.WindowHeight;

            SizeChanged += (s, e) =>
            {
                if (_isHudMode)
                    EnsureHudModulesInBounds();
            };

            // Load models in background
            _ = LoadModelsAsync();
            
            // Save state on close

            Closing += (s, e) =>
            {
                UnregisterGlobalHotkey();
                if (_isHudMode && !_windowedBounds.IsEmpty)
                {
                    _settings.WindowTop = _windowedBounds.Top;
                    _settings.WindowLeft = _windowedBounds.Left;
                    _settings.WindowWidth = _windowedBounds.Width;
                    _settings.WindowHeight = _windowedBounds.Height;
                }
                else
                {
                    _settings.WindowTop = Top;
                    _settings.WindowLeft = Left;
                    _settings.WindowWidth = Width;
                    _settings.WindowHeight = Height;
                }
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

        private void ExcludeFromCapture()
        {
            var helper = new WindowInteropHelper(this);
            
            // Hide from screen capture
            if (!SetWindowDisplayAffinity(helper.Handle, WDA_EXCLUDEFROMCAPTURE))
            {
                SetWindowDisplayAffinity(helper.Handle, WDA_MONITOR);
            }
            
            // Hide from Alt+Tab by adding WS_EX_TOOLWINDOW style
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle);
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
            CycleOverlayMode();
        }

        private bool IsOverlayHidden() => !IsVisible || WindowState == WindowState.Minimized;

        private void ShowOverlay()
        {
            if (!IsVisible)
                Show();

            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Activate();
        }

        private void HideOverlay()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Hide();
        }

        private OverlayMode GetOverlayMode()
        {
            if (IsOverlayHidden())
                return OverlayMode.Hidden;
            return _isHudMode ? OverlayMode.Hud : OverlayMode.Windowed;
        }

        private void CycleOverlayMode()
        {
            switch (GetOverlayMode())
            {
                case OverlayMode.Hidden:
                    ExitHudMode();
                    ShowOverlay();
                    break;
                case OverlayMode.Windowed:
                    EnterHudMode();
                    break;
                case OverlayMode.Hud:
                    ExitHudMode();
                    HideOverlay();
                    break;
            }
        }

        private void SetClickThrough(bool enabled)
        {
            var helper = new WindowInteropHelper(this);
            int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            exStyle = enabled
                ? exStyle | WS_EX_TRANSPARENT
                : exStyle & ~WS_EX_TRANSPARENT;
            SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle);
            SetWindowPos(helper.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        private void EnterHudMode()
        {
            if (_isHudMode)
                return;

            BeginModeTransition();

            if (WindowState == WindowState.Normal)
            {
                _windowedBounds = new Rect(Left, Top, Width, Height);
            }

            _isHudMode = true;
            SettingsPanel.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;
            ApplyHudWindowBounds();

            RootShell.Margin = new Thickness(0);
            RootShell.CornerRadius = new CornerRadius(0);
            RootShell.Background = Brushes.Transparent;
            RootShell.BorderBrush = Brushes.Transparent;
            RootShell.BorderThickness = new Thickness(0);
            RootShell.Effect = null;

            TitleBarBorder.Visibility = Visibility.Collapsed;
            ControlsBarBorder.Visibility = Visibility.Collapsed;
            StatusBarBorder.Visibility = Visibility.Collapsed;
            SplitViewGrid.Visibility = Visibility.Collapsed;
            HudCanvas.Visibility = Visibility.Visible;
            TranscriptHeaderRow.Visibility = Visibility.Collapsed;
            SplitSeparator.Visibility = Visibility.Collapsed;
            AiHeaderRow.Visibility = Visibility.Collapsed;
            ModelFilterBar.Visibility = Visibility.Collapsed;
            ModelListView.Visibility = Visibility.Collapsed;
            ClaudeModelIndicator.Visibility = Visibility.Collapsed;
            ExtractControlsRow.Visibility = Visibility.Collapsed;
            AiStatusText.Visibility = Visibility.Collapsed;
            HudExtractButton.Visibility = Visibility.Collapsed;

            TranscriptPanelBackdrop.Background = Brushes.Transparent;
            TranscriptPanelBackdrop.BorderBrush = Brushes.Transparent;
            QaPanelBackdrop.Background = Brushes.Transparent;
            QaPanelBackdrop.BorderBrush = Brushes.Transparent;
            SetHudTextCardTheme(true);

            Topmost = true;
            SetClickThrough(false);
            HudExtractButton.IsEnabled = ExtractButton.IsEnabled;
            HudExtractButton.Content = "🧠 Extract + Answer";
            Dispatcher.InvokeAsync(EnsureHudModulesInBounds, DispatcherPriority.Loaded);
            FinishModeTransition();
        }

        private void ExitHudMode()
        {
            if (!_isHudMode)
                return;

            BeginModeTransition();

            SetClickThrough(false);
            _isHudMode = false;

            RootShell.Margin = new Thickness(8);
            RootShell.CornerRadius = new CornerRadius(12);
            RootShell.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0D, 0x11, 0x17));
            RootShell.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            RootShell.BorderThickness = new Thickness(1);
            RootShell.Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 4,
                Opacity = 0.5,
                Color = Colors.Black
            };

            TitleBarBorder.Visibility = Visibility.Visible;
            ControlsBarBorder.Visibility = Visibility.Visible;
            StatusBarBorder.Visibility = Visibility.Visible;
            SplitViewGrid.Visibility = Visibility.Visible;
            HudCanvas.Visibility = Visibility.Collapsed;
            TranscriptHeaderRow.Visibility = Visibility.Visible;
            SplitSeparator.Visibility = Visibility.Visible;
            AiHeaderRow.Visibility = Visibility.Visible;
            ModelFilterBar.Visibility = Visibility.Visible;
            ExtractControlsRow.Visibility = Visibility.Visible;
            AiStatusText.Visibility = Visibility.Visible;
            HudExtractButton.Visibility = Visibility.Collapsed;

            TranscriptPanelBackdrop.Background = Brushes.Transparent;
            TranscriptPanelBackdrop.BorderBrush = Brushes.Transparent;
            QaPanelBackdrop.Background = Brushes.Transparent;
            QaPanelBackdrop.BorderBrush = Brushes.Transparent;
            SetHudTextCardTheme(false);

            UpdateModelBrowserVisibility();

            if (!_windowedBounds.IsEmpty)
            {
                WindowState = WindowState.Normal;
                Left = _windowedBounds.Left;
                Top = _windowedBounds.Top;
                Width = _windowedBounds.Width;
                Height = _windowedBounds.Height;
            }

            Topmost = _isPinned;
            FinishModeTransition();
        }

        private void BeginModeTransition()
        {
            RootShell.BeginAnimation(OpacityProperty, null);
            RootShell.Opacity = 0;
        }

        private void FinishModeTransition()
        {
            Dispatcher.InvokeAsync(() =>
            {
                var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                RootShell.BeginAnimation(OpacityProperty, fadeIn);
            }, DispatcherPriority.Loaded);
        }

        private void ApplyHudWindowBounds()
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
        }

        private void SetHudTextCardTheme(bool hudEnabled)
        {
            SetBrushColor("TranscriptCardBackgroundBrush",
                hudEnabled ? Color.FromArgb(0xD8, 0x00, 0x00, 0x00) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            SetBrushColor("QaCardBackgroundBrush",
                hudEnabled ? Color.FromArgb(0xD8, 0x00, 0x00, 0x00) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            SetBrushColor("QaAnswerBackgroundBrush",
                hudEnabled ? Color.FromArgb(0xC8, 0x00, 0x00, 0x00) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            SetBrushColor("FollowUpCardBackgroundBrush",
                hudEnabled ? Color.FromArgb(0xD2, 0x00, 0x00, 0x00) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
            SetBrushColor("FollowUpAnswerBackgroundBrush",
                hudEnabled ? Color.FromArgb(0xC4, 0x00, 0x00, 0x00) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        }

        private void SetBrushColor(string resourceKey, Color color)
        {
            if (Resources[resourceKey] is SolidColorBrush brush)
                brush.Color = color;
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
        private bool IsLmStudio => _settings.AiProvider == "LMStudio";
        private bool IsOpenRouter => !IsClaude && !IsLmStudio;

        private IEnumerable<QuestionAnswer> AllQuestions()
        {
            foreach (var qa in _qaItems)
            {
                yield return qa;
                foreach (var followUp in qa.FollowUps)
                    yield return followUp;
            }
        }

        private static string NormalizeQuestionKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var chars = text
                .Trim()
                .ToLowerInvariant()
                .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                .ToArray();
            return new string(chars);
        }

        private bool IsDuplicateQuestion(string questionText)
        {
            var key = NormalizeQuestionKey(questionText);
            return AllQuestions().Any(existing => NormalizeQuestionKey(existing.Question) == key);
        }

        private List<string> BuildKnownQuestionTexts()
        {
            return AllQuestions()
                .Select(x => x.Question)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private QuestionAnswer? FindMainQuestionByText(string questionText)
        {
            var key = NormalizeQuestionKey(questionText);
            if (string.IsNullOrWhiteSpace(key)) return null;

            var exact = _qaItems.FirstOrDefault(q => NormalizeQuestionKey(q.Question) == key);
            if (exact != null) return exact;

            return _qaItems.FirstOrDefault(q =>
                NormalizeQuestionKey(q.Question).Contains(key, StringComparison.OrdinalIgnoreCase) ||
                key.Contains(NormalizeQuestionKey(q.Question), StringComparison.OrdinalIgnoreCase));
        }

        private void AddClaudeQuestions(
            List<ExtractedQuestion> extractedQuestions,
            List<QuestionAnswer> newQas,
            ref int startNum)
        {
            foreach (var extracted in extractedQuestions)
            {
                var qText = extracted.Question?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(qText) || IsDuplicateQuestion(qText))
                    continue;

                QuestionAnswer? parent = null;
                if (extracted.IsFollowUp)
                {
                    parent = FindMainQuestionByText(extracted.ParentQuestion);
                    parent ??= _qaItems.LastOrDefault();
                }

                var qa = new QuestionAnswer
                {
                    Question = qText,
                    ParagraphAnswer = "⏳ Generating...",
                    IsExpanded = true,
                    IsFollowUp = extracted.IsFollowUp && parent != null,
                    ParentQuestion = parent?.Question ?? string.Empty
                };

                if (qa.IsFollowUp && parent != null)
                {
                    qa.FollowUpNumber = parent.FollowUps.Count + 1;
                    parent.FollowUps.Add(qa);
                }
                else
                {
                    qa.Number = ++startNum;
                    _qaItems.Add(qa);
                }

                newQas.Add(qa);
            }
        }

        private void AddOpenRouterQuestions(List<QuestionAnswer> parsedItems, ref int startNum)
        {
            foreach (var item in parsedItems)
            {
                var question = item.Question?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(question) || IsDuplicateQuestion(question))
                    continue;

                QuestionAnswer? parent = null;
                if (item.IsFollowUp)
                {
                    parent = FindMainQuestionByText(item.ParentQuestion);
                    parent ??= _qaItems.LastOrDefault();
                }

                item.Question = question;
                if (item.IsFollowUp && parent != null)
                {
                    item.ParentQuestion = parent.Question;
                    item.FollowUpNumber = parent.FollowUps.Count + 1;
                    parent.FollowUps.Add(item);
                }
                else
                {
                    item.IsFollowUp = false;
                    item.Number = ++startNum;
                    _qaItems.Add(item);
                }
            }
        }

        private string GetAiApiKey()
        {
            if (IsClaude) return _settings.ClaudeApiKey;
            if (IsLmStudio) return _settings.LmStudioApiKey;
            return _settings.OpenRouterApiKey;
        }

        private string GetAiModelId()
        {
            if (IsClaude)
                return "claude-haiku-4-5";
            return _selectedModel?.Id ?? GetSavedModelIdForCurrentProvider();
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
            SettingsLmStudioApiKey.Text = _settings.LmStudioApiKey;
            SettingsLmStudioBaseUrl.Text = string.IsNullOrWhiteSpace(_settings.LmStudioBaseUrl)
                ? "http://127.0.0.1:1234"
                : _settings.LmStudioBaseUrl;
            SettingsAssemblyKey.Text = _settings.AssemblyAiApiKey;
            SettingsDeepgramKey.Text = _settings.DeepgramApiKey;
            SettingsJobDesc.Text = _settings.JobDescription;
            SettingsResume.Text = _settings.Resume;

            // Backward compatibility for existing settings.json files
            if (!string.IsNullOrWhiteSpace(_settings.SelectedModelId))
            {
                if (string.IsNullOrWhiteSpace(_settings.SelectedOpenRouterModelId))
                    _settings.SelectedOpenRouterModelId = _settings.SelectedModelId;
                if (string.IsNullOrWhiteSpace(_settings.SelectedLmStudioModelId))
                    _settings.SelectedLmStudioModelId = _settings.SelectedModelId;
            }

            // Load AI provider selection
            SettingsAiProvider.SelectedIndex = _settings.AiProvider switch
            {
                "Claude" => 1,
                "LMStudio" => 2,
                _ => 0
            };
            UpdateModelBrowserVisibility();

            if (string.IsNullOrEmpty(_settings.AssemblyAiApiKey) && string.IsNullOrEmpty(_settings.DeepgramApiKey))
                UpdateStatus("⚠ Set API key(s) in Settings", isError: true);
            else
                UpdateStatus("Ready — Press Start to begin");
        }

        private void UpdateModelBrowserVisibility()
        {
            var isClaude = IsClaude;
             
            // Toggle visibility of model browser vs Claude indicator
            ModelFilterBar.Visibility = isClaude ? Visibility.Collapsed : Visibility.Visible;
            ModelListView.Visibility = isClaude ? Visibility.Collapsed : Visibility.Visible;
            ClaudeModelIndicator.Visibility = isClaude ? Visibility.Visible : Visibility.Collapsed;

            // Enable extract buttons when Claude is selected (no model selection needed)
            ExtractButton.IsEnabled = isClaude || _selectedModel != null;
            AutoExtractButton.IsEnabled = isClaude || _selectedModel != null;
            HudExtractButton.IsEnabled = ExtractButton.IsEnabled;
        }



        // ── Model Browser ──

        private async Task LoadModelsAsync()
        {
            try
            {
                if (IsClaude)
                {
                    _allModels = new List<OpenRouterModel>();
                    Dispatcher.Invoke(() =>
                    {
                        ModelListView.ItemsSource = null;
                        _selectedModel = null;
                        UpdateModelBrowserVisibility();
                        UpdateStatus("Ready — Claude selected");
                    });
                    return;
                }

                UpdateStatus(IsLmStudio ? "Loading LM Studio models…" : "Loading OpenRouter models…");
                _allModels = IsLmStudio
                    ? await _lmStudioService.FetchModelsAsync(_settings.LmStudioBaseUrl, _settings.LmStudioApiKey)
                    : await _openRouterService.FetchModelsAsync();
                Dispatcher.Invoke(() =>
                {
                    ApplyModelFilters();
                    var providerName = IsLmStudio ? "LM Studio" : "OpenRouter";
                    UpdateStatus($"Ready — {_allModels.Count} {providerName} models loaded");
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

            _selectedModel = null;
            ModelListView.ItemsSource = sorted;

            // Restore selection if saved
            var savedModelId = GetSavedModelIdForCurrentProvider();
            if (!string.IsNullOrEmpty(savedModelId))
            {
                var saved = sorted.FirstOrDefault(m => m.Id == savedModelId);
                if (saved != null)
                {
                    ModelListView.SelectedItem = saved;
                    ModelListView.ScrollIntoView(saved);
                }
            }

            ExtractButton.IsEnabled = IsClaude || _selectedModel != null;
            AutoExtractButton.IsEnabled = IsClaude || _selectedModel != null;
            HudExtractButton.IsEnabled = ExtractButton.IsEnabled;
        }

        // ── UI Event Handlers ──

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void HudContainer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isHudMode || e.ChangedButton != MouseButton.Left)
                return;

            if (e.OriginalSource is not DependencyObject source)
                return;

            if (FindAncestor<System.Windows.Controls.Button>(source) != null ||
                FindAncestor<System.Windows.Controls.TextBox>(source) != null ||
                FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(source) != null ||
                FindAncestor<System.Windows.Controls.ComboBox>(source) != null ||
                FindAncestor<System.Windows.Controls.ListView>(source) != null ||
                FindAncestor<System.Windows.Controls.CheckBox>(source) != null)
            {
                return;
            }

            DragMove();
        }

        private void HudModuleHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isHudMode || sender is not FrameworkElement header || header.Tag is not Border module)
                return;

            _activeHudModule = module;
            _activeHudHeader = header;
            _hudDragStartPoint = e.GetPosition(HudCanvas);
            _hudDragStartLeft = Canvas.GetLeft(module);
            _hudDragStartTop = Canvas.GetTop(module);
            if (double.IsNaN(_hudDragStartLeft)) _hudDragStartLeft = 0;
            if (double.IsNaN(_hudDragStartTop)) _hudDragStartTop = 0;
            header.CaptureMouse();
            e.Handled = true;
        }

        private void HudModuleHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (_activeHudModule == null || _activeHudHeader == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var current = e.GetPosition(HudCanvas);
            var newLeft = _hudDragStartLeft + (current.X - _hudDragStartPoint.X);
            var newTop = _hudDragStartTop + (current.Y - _hudDragStartPoint.Y);

            var maxLeft = Math.Max(0, HudCanvas.ActualWidth - _activeHudModule.ActualWidth);
            var maxTop = Math.Max(0, HudCanvas.ActualHeight - _activeHudModule.ActualHeight);
            newLeft = Math.Max(0, Math.Min(maxLeft, newLeft));
            newTop = Math.Max(0, Math.Min(maxTop, newTop));

            Canvas.SetLeft(_activeHudModule, newLeft);
            Canvas.SetTop(_activeHudModule, newTop);
            e.Handled = true;
        }

        private void HudModuleHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_activeHudHeader != null)
                _activeHudHeader.ReleaseMouseCapture();
            _activeHudModule = null;
            _activeHudHeader = null;
            e.Handled = true;
        }

        private void HudModuleResize_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_isHudMode || sender is not FrameworkElement handle || handle.Tag is not Border module)
                return;

            _activeHudResizeModule = module;
            _activeHudResizeHandle = handle;
            _hudResizeStartPoint = e.GetPosition(HudCanvas);
            _hudResizeStartWidth = module.ActualWidth > 0 ? module.ActualWidth : module.Width;
            _hudResizeStartHeight = module.ActualHeight > 0 ? module.ActualHeight : module.Height;
            handle.CaptureMouse();
            e.Handled = true;
        }

        private void HudModuleResize_MouseMove(object sender, MouseEventArgs e)
        {
            if (_activeHudResizeModule == null || _activeHudResizeHandle == null || e.LeftButton != MouseButtonState.Pressed)
                return;

            var current = e.GetPosition(HudCanvas);
            var left = Canvas.GetLeft(_activeHudResizeModule);
            var top = Canvas.GetTop(_activeHudResizeModule);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            const double minWidth = 260;
            const double minHeight = 180;
            var maxWidth = Math.Max(minWidth, HudCanvas.ActualWidth - left);
            var maxHeight = Math.Max(minHeight, HudCanvas.ActualHeight - top);
            var newWidth = _hudResizeStartWidth + (current.X - _hudResizeStartPoint.X);
            var newHeight = _hudResizeStartHeight + (current.Y - _hudResizeStartPoint.Y);

            _activeHudResizeModule.Width = Math.Max(minWidth, Math.Min(maxWidth, newWidth));
            _activeHudResizeModule.Height = Math.Max(minHeight, Math.Min(maxHeight, newHeight));
            e.Handled = true;
        }

        private void HudModuleResize_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_activeHudResizeHandle != null)
                _activeHudResizeHandle.ReleaseMouseCapture();
            _activeHudResizeModule = null;
            _activeHudResizeHandle = null;
            e.Handled = true;
        }

        private void InitializeHudModulesIfNeeded()
        {
            if (_hudModulesInitialized)
                return;

            var canvasWidth = HudCanvas.ActualWidth > 0 ? HudCanvas.ActualWidth : SplitViewGrid.ActualWidth;
            var canvasHeight = HudCanvas.ActualHeight > 0 ? HudCanvas.ActualHeight : SplitViewGrid.ActualHeight;
            if (canvasWidth <= 0 || canvasHeight <= 0)
                return;

            const double gap = 18;
            const double sidePad = 24;
            var moduleWidth = Math.Min(420, Math.Max(280, (canvasWidth - (sidePad * 2) - gap) / 2));
            var moduleHeight = Math.Min(360, Math.Max(220, canvasHeight * 0.45));

            HudTranscriptModule.Width = moduleWidth;
            HudAnswersModule.Width = moduleWidth;
            HudTranscriptModule.Height = moduleHeight;
            HudAnswersModule.Height = moduleHeight;

            Canvas.SetLeft(HudTranscriptModule, sidePad);
            Canvas.SetTop(HudTranscriptModule, 12);
            Canvas.SetLeft(HudAnswersModule, sidePad + moduleWidth + gap);
            Canvas.SetTop(HudAnswersModule, 12);

            _hudModulesInitialized = true;
        }

        private void EnsureHudModulesInBounds()
        {
            if (!_isHudMode)
                return;

            InitializeHudModulesIfNeeded();

            var canvasWidth = HudCanvas.ActualWidth;
            var canvasHeight = HudCanvas.ActualHeight;
            if (canvasWidth <= 0 || canvasHeight <= 0)
                return;

            var maxModuleHeight = Math.Max(180, canvasHeight - 24);
            HudTranscriptModule.Height = Math.Min(HudTranscriptModule.Height, maxModuleHeight);
            HudAnswersModule.Height = Math.Min(HudAnswersModule.Height, maxModuleHeight);

            ClampHudModulePosition(HudTranscriptModule, canvasWidth, canvasHeight);
            ClampHudModulePosition(HudAnswersModule, canvasWidth, canvasHeight);
        }

        private static void ClampHudModulePosition(Border module, double canvasWidth, double canvasHeight)
        {
            var left = Canvas.GetLeft(module);
            var top = Canvas.GetTop(module);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            var maxLeft = Math.Max(0, canvasWidth - module.ActualWidth);
            var maxTop = Math.Max(0, canvasHeight - module.ActualHeight);
            Canvas.SetLeft(module, Math.Max(0, Math.Min(maxLeft, left)));
            Canvas.SetTop(module, Math.Max(0, Math.Min(maxTop, top)));
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T typed)
                    return typed;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => HideOverlay();

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
            ExtractButton.IsEnabled = IsClaude || _selectedModel != null;
            AutoExtractButton.IsEnabled = IsClaude || _selectedModel != null;
            HudExtractButton.IsEnabled = ExtractButton.IsEnabled;

            if (_selectedModel != null)
            {
                SaveSelectedModelIdForCurrentProvider(_selectedModel.Id);
                _settings.Save();
                QueueLmStudioWarmup();
            }
        }

        private async void Extract_Click(object sender, RoutedEventArgs e)
        {
            if (!IsClaude && _selectedModel == null) return;
            _autoStreamDebounceTimer.Stop();
            _autoStreamPendingSince = DateTime.MinValue;

            var apiKey = GetAiApiKey();
            if (!IsLmStudio && string.IsNullOrEmpty(apiKey))
            {
                var provider = IsClaude ? "Claude" : "OpenRouter";
                UpdateStatus($"⚠ Set {provider} API key in Settings", isError: true);
                return;
            }

            var transcript = BuildExtractionTranscriptText();
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
                HudExtractButton.IsEnabled = false;
                HudExtractButton.Content = "⏳ Working…";
                AiStatusText.Text = $"Extracting questions with {modelName}…";
                UpdateStatus($"Extracting questions with {modelName}…");

                List<ExtractedQuestion> extractedQuestions;
                if (IsClaude)
                {
                    extractedQuestions = await _claudeService.ExtractQuestionTextsOnlyAsync(
                        apiKey, modelId, transcript, BuildKnownQuestionTexts());
                }
                else if (IsLmStudio)
                {
                    extractedQuestions = await _lmStudioService.ExtractQuestionTextsOnlyAsync(
                        _settings.LmStudioBaseUrl, apiKey, modelId, transcript, BuildKnownQuestionTexts());
                }
                else
                {
                    extractedQuestions = await _openRouterService.ExtractQuestionTextsOnlyAsync(
                        apiKey, modelId, transcript, BuildKnownQuestionTexts());
                }

                _lastExtractedEntryIndex = _transcriptManager.Entries.Count;
                if (extractedQuestions.Count == 0)
                {
                    AiStatusText.Text = "No new questions detected.";
                }
                else
                {
                    int startNum = _qaItems.Count;
                    var newQas = new List<QuestionAnswer>();
                    AddClaudeQuestions(extractedQuestions, newQas, ref startNum);
                    EnsureQaListBound();

                    if (newQas.Count == 0)
                    {
                        AiStatusText.Text = "No new questions detected.";
                        return;
                    }

                    // QaList.ItemsSource is already bound to _qaItems in constructor
                    AiStatusText.Text = $"{newQas.Count} questions found. Streaming first answer...";

                    _ = StartStreamingAnswersWithPriority(newQas, apiKey, modelId, BuildAnswerContextText());
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
                HudExtractButton.Content = "🧠 Extract + Answer";
                UpdateModelBrowserVisibility();
            }
        }

        private async Task ProcessStreamingAnswer(
            QuestionAnswer qa,
            string apiKey,
            string modelId,
            string transcript,
            QuestionAnswer? parentQa = null,
            TaskCompletionSource<bool>? firstChunkSignal = null)
        {
            try
            {
                var fullAnswer = new StringBuilder();
                bool hasStarted = false;
                var parentAnswerContext =
                    parentQa != null &&
                    !string.IsNullOrWhiteSpace(parentQa.ParagraphAnswer) &&
                    !parentQa.ParagraphAnswer.StartsWith("⏳", StringComparison.Ordinal)
                        ? parentQa.ParagraphAnswer
                        : null;

                var stream = IsLmStudio
                    ? _lmStudioService.StreamAnswerAsync(
                        _settings.LmStudioBaseUrl, apiKey, modelId, qa.Question, transcript, _settings.JobDescription, _settings.Resume,
                        parentQa?.Question, parentAnswerContext)
                    : IsClaude
                        ? _claudeService.StreamAnswerAsync(
                        apiKey, modelId, qa.Question, transcript, _settings.JobDescription, _settings.Resume,
                        parentQa?.Question, parentAnswerContext)
                        : _openRouterService.StreamAnswerAsync(
                            apiKey, modelId, qa.Question, transcript, _settings.JobDescription, _settings.Resume,
                            parentQa?.Question, parentAnswerContext);

                await foreach (var chunk in stream)
                {
                    if (!hasStarted)
                    {
                        qa.ParagraphAnswer = ""; // Clear the "Thinking..." placeholder
                        hasStarted = true;
                        firstChunkSignal?.TrySetResult(true);
                    }

                    qa.ParagraphAnswer += chunk;
                    fullAnswer.Append(chunk);
                    
                    // Auto-scroll logic moved here to ensure it's on UI thread if needed,
                    // but since ParagraphAnswer triggers PropertyChanged, we just need to 
                    // ensure the scroller follows.
                    if (qa == _qaItems.LastOrDefault())
                    {
                        Dispatcher.Invoke(() => QaScroller.ScrollToEnd());
                        if (_isHudMode)
                            Dispatcher.Invoke(() => HudQaScroller.ScrollToEnd());
                    }
                }

                // Tracking answered questions to avoid duplicates in future extractions
                var answered = $"Q: {qa.Question}\nA: {fullAnswer}";
                if (IsLmStudio)
                    _lmStudioService.PreviouslyAnswered.Add(answered);
                else if (IsClaude)
                    _claudeService.PreviouslyAnswered.Add(answered);
                else
                    _openRouterService.PreviouslyAnswered.Add(answered);

                if (!hasStarted)
                    firstChunkSignal?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                qa.ParagraphAnswer = $"[Error streaming answer: {ex.Message}]";
                firstChunkSignal?.TrySetResult(true);
            }
        }

        private void ClearAiHistory_Click(object sender, RoutedEventArgs e)
        {
            _openRouterService.ClearHistory();
            _claudeService.ClearHistory();
            _lmStudioService.ClearHistory();
            _autoStreamDebounceTimer.Stop();
            _autoStreamPendingSince = DateTime.MinValue;
            _qaItems.Clear();
            _lastExtractedEntryIndex = 0;
            EnsureQaListBound();
            AiStatusText.Text = IsClaude ? "Click Extract Questions" : "Select a model and click Extract Questions";
            UpdateStatus("AI history cleared");
        }

        // ── Auto-Extract ──

        private void AutoExtract_Click(object sender, RoutedEventArgs e)
        {
            _autoExtractEnabled = !_autoExtractEnabled;

            if (_autoExtractEnabled)
            {
                AutoExtractButton.Content = "⚡ Auto Stream ●";
                AutoExtractButton.Foreground = FindResource("AccentGreenBrush") as SolidColorBrush
                    ?? new SolidColorBrush(Color.FromRgb(0, 210, 130));
                _lastExtractedEntryIndex = _transcriptManager.Entries.Count;
                _autoStreamPendingSince = DateTime.MinValue;
                _autoStreamDebounceTimer.Stop();
                UpdateStatus($"Auto-stream ON — waits for context-safe boundary ({MinNewWords}+ words)");
            }
            else
            {
                AutoExtractButton.Content = "⚡ Auto Stream";
                AutoExtractButton.Foreground = FindResource("TextSecondaryBrush") as SolidColorBrush
                    ?? new SolidColorBrush(Colors.Gray);
                _autoStreamDebounceTimer.Stop();
                _autoStreamPendingSince = DateTime.MinValue;
                UpdateStatus("Auto-stream OFF");
            }
        }

        private void OnTranscriptChanged()
        {
            if (!_autoExtractEnabled || _isExtracting) return;
            var newEntries = GetNewFinalEntriesSinceLastExtraction();
            var newWordCount = CountWords(string.Join(" ", newEntries.Select(e => e.Text)));
            if (newWordCount < MinNewWords) return;

            if (_autoStreamPendingSince == DateTime.MinValue)
                _autoStreamPendingSince = DateTime.UtcNow;

            _autoStreamDebounceTimer.Stop();
            _autoStreamDebounceTimer.Start();
        }

        /// <summary>
        /// Builds only the NEW transcript since last extraction and sends it.
        /// Only fires if there are enough new words to be worth an API call.
        /// </summary>
        private async Task RunAutoExtractAsync()
        {
            if (_isExtracting || (!IsClaude && _selectedModel == null)) return;

            var apiKey = GetAiApiKey();
            if (!IsLmStudio && string.IsNullOrEmpty(apiKey)) return;

            // Build incremental transcript (only new entries)
            var entries = _transcriptManager.Entries;
            var newEntries = GetNewFinalEntriesSinceLastExtraction();
            if (newEntries.Count == 0) return;

            var newText = string.Join("\n",
                newEntries.Select(e => $"[{e.Speaker}] {e.Text}"));

            // Check minimum word threshold
            int wordCount = CountWords(newText);
            if (wordCount < MinNewWords) return;

            var lastEntryText = newEntries[^1].Text?.Trim() ?? string.Empty;
            var waitedMs = _autoStreamPendingSince == DateTime.MinValue
                ? 0
                : (DateTime.UtcNow - _autoStreamPendingSince).TotalMilliseconds;
            var canForceBoundary = waitedMs >= AutoStreamMaxWaitMs;
            if (!LooksLikeBoundarySafe(lastEntryText) && !canForceBoundary)
            {
                AiStatusText.Text = "Auto-stream waiting for full question context…";
                _autoStreamDebounceTimer.Stop();
                _autoStreamDebounceTimer.Start();
                return;
            }

            var overlapStart = Math.Max(0, _lastExtractedEntryIndex - AutoStreamOverlapEntries);
            var extractionContext = string.Join("\n",
                entries.Skip(overlapStart)
                      .Where(e => e.IsFinal && !string.IsNullOrWhiteSpace(e.Text))
                      .Select(e => $"[{e.Speaker}] {e.Text}"));
            extractionContext = TrimToLastChars(extractionContext, ExtractionContextMaxChars);

            _isExtracting = true;
            try
            {
                var modelId = GetAiModelId();
                var modelName = IsClaude ? "Claude 4.5 Haiku" : _selectedModel?.Name;
                AiStatusText.Text = $"Auto-extracting ({wordCount} new words)…";

                if (IsClaude || IsLmStudio || IsOpenRouter)
                {
                    var extractedQuestions = IsClaude
                        ? await _claudeService.ExtractQuestionTextsOnlyAsync(
                            apiKey, modelId, extractionContext, BuildKnownQuestionTexts())
                        : IsLmStudio
                            ? await _lmStudioService.ExtractQuestionTextsOnlyAsync(
                                _settings.LmStudioBaseUrl, apiKey, modelId, extractionContext, BuildKnownQuestionTexts())
                            : await _openRouterService.ExtractQuestionTextsOnlyAsync(
                                apiKey, modelId, extractionContext, BuildKnownQuestionTexts());
                    _lastExtractedEntryIndex = entries.Count;
                    _autoStreamPendingSince = DateTime.MinValue;

                    if (extractedQuestions.Count > 0)
                    {
                        int startNum = _qaItems.Count;
                        var newQas = new List<QuestionAnswer>();
                        AddClaudeQuestions(extractedQuestions, newQas, ref startNum);

                        if (newQas.Count > 0)
                        {
                            EnsureQaListBound();
                            AiStatusText.Text = $"{newQas.Count} questions found (auto). Streaming first answer...";
                            _ = StartStreamingAnswersWithPriority(newQas, apiKey, modelId, BuildAnswerContextText());
                        }
                        else
                        {
                            AiStatusText.Text = "No new questions detected.";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AiStatusText.Text = $"Auto-extract error: {ex.Message}";
                _autoStreamPendingSince = DateTime.MinValue;
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
            var allAnswers = AllQuestions().ToList();
            _allExpanded = allAnswers.Any(qa => !qa.IsExpanded);

            foreach (var qa in _qaItems)
            {
                qa.IsExpanded = _allExpanded;
                foreach (var followUp in qa.FollowUps)
                    followUp.IsExpanded = _allExpanded;
            }
            ToggleAllButton.Content = _allExpanded ? "👁 Hide All" : "👁 Show All";
        }

        private async Task StartStreamingAnswersWithPriority(
            List<QuestionAnswer> newQas,
            string apiKey,
            string modelId,
            string transcriptContext)
        {
            if (newQas.Count == 0) return;

            var first = newQas[0];
            var firstParent = ResolveParentForStreaming(first);
            var firstChunkSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = ProcessStreamingAnswer(first, apiKey, modelId, transcriptContext, firstParent, firstChunkSignal);

            if (newQas.Count == 1) return;

            await Task.WhenAny(firstChunkSignal.Task, Task.Delay(DeferredStreamStartMs));
            for (int i = 1; i < newQas.Count; i++)
            {
                var qa = newQas[i];
                var parent = ResolveParentForStreaming(qa);
                _ = ProcessStreamingAnswer(qa, apiKey, modelId, transcriptContext, parent);
            }
        }

        private void EnsureQaListBound()
        {
            if (!ReferenceEquals(QaList.ItemsSource, _qaItems))
                QaList.ItemsSource = _qaItems;
            if (!ReferenceEquals(HudQaList.ItemsSource, _qaItems))
                HudQaList.ItemsSource = _qaItems;
        }

        private QuestionAnswer? ResolveParentForStreaming(QuestionAnswer qa)
        {
            if (!qa.IsFollowUp)
                return null;

            return _qaItems.FirstOrDefault(x =>
                NormalizeQuestionKey(x.Question) == NormalizeQuestionKey(qa.ParentQuestion));
        }

        private string BuildExtractionTranscriptText()
        {
            var entries = _transcriptManager.Entries;
            var incremental = string.Join("\n",
                entries.Skip(_lastExtractedEntryIndex)
                    .Where(e => e.IsFinal)
                    .Select(e => $"[{e.Speaker}] {e.Text}")).Trim();

            var incrementalWordCount = incremental.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (incrementalWordCount >= MinManualIncrementalWords)
                return TrimToLastChars(incremental, ExtractionContextMaxChars);

            return BuildRecentTranscriptText(ExtractionContextMaxChars);
        }

        private string BuildAnswerContextText()
        {
            return BuildRecentTranscriptText(AnswerContextMaxChars);
        }

        private string BuildRecentTranscriptText(int maxChars)
        {
            var lines = _transcriptManager.Entries
                .Where(e => e.IsFinal)
                .Select(e => $"[{e.Speaker}] {e.Text}")
                .ToList();

            if (lines.Count == 0) return string.Empty;

            var selected = new List<string>();
            int totalChars = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                int lineChars = line.Length + 1;
                if (totalChars + lineChars > maxChars && selected.Count > 0)
                    break;

                selected.Add(line);
                totalChars += lineChars;
            }

            selected.Reverse();
            return string.Join("\n", selected).Trim();
        }

        private static string TrimToLastChars(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
                return text;
            return text.Substring(text.Length - maxChars);
        }

        private List<TranscriptEntry> GetNewFinalEntriesSinceLastExtraction()
        {
            return _transcriptManager.Entries
                .Skip(_lastExtractedEntryIndex)
                .Where(e => e.IsFinal && !string.IsNullOrWhiteSpace(e.Text))
                .ToList();
        }

        private static int CountWords(string text)
        {
            return (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        private static bool LooksLikeBoundarySafe(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var trimmed = text.Trim();
            while (trimmed.Length > 0 && "\"')]}".Contains(trimmed[^1]))
                trimmed = trimmed[..^1];
            if (trimmed.Length == 0) return false;

            char end = trimmed[^1];
            if (end == '.' || end == '?' || end == '!')
                return true;

            if (end == ',' || end == ';' || end == ':')
                return false;

            var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 4)
                return false;

            var trailing = words[^1].ToLowerInvariant();
            return trailing != "and" &&
                   trailing != "or" &&
                   trailing != "but" &&
                   trailing != "so" &&
                   trailing != "because" &&
                   trailing != "that" &&
                   trailing != "which" &&
                   trailing != "to" &&
                   trailing != "for" &&
                   trailing != "with" &&
                   trailing != "of" &&
                   trailing != "in" &&
                   trailing != "on" &&
                   trailing != "at" &&
                   trailing != "from" &&
                   trailing != "if" &&
                   trailing != "when" &&
                   trailing != "then";
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

                foreach (var followUp in qa.FollowUps)
                {
                    sb.AppendLine($"Q{qa.Number}.F{followUp.FollowUpNumber}: {followUp.Question}".Trim());
                    if (!string.IsNullOrWhiteSpace(followUp.ParagraphAnswer))
                    {
                        sb.AppendLine();
                        sb.AppendLine(followUp.ParagraphAnswer.Trim());
                    }

                    if (!string.IsNullOrWhiteSpace(followUp.KeyPoints))
                    {
                        sb.AppendLine();
                        sb.AppendLine(followUp.KeyPoints.Trim());
                    }

                    sb.AppendLine();
                    sb.AppendLine(new string('-', 24));
                    sb.AppendLine();
                }
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
            _settings.LmStudioApiKey = SettingsLmStudioApiKey.Text.Trim();
            _settings.LmStudioBaseUrl = SettingsLmStudioBaseUrl.Text.Trim();
            _settings.AssemblyAiApiKey = SettingsAssemblyKey.Text.Trim();
            _settings.DeepgramApiKey = SettingsDeepgramKey.Text.Trim();
            _settings.JobDescription = SettingsJobDesc.Text.Trim();
            _settings.Resume = SettingsResume.Text.Trim();
            _settings.AiProvider = SettingsAiProvider.SelectedIndex switch
            {
                1 => "Claude",
                2 => "LMStudio",
                _ => "OpenRouter"
            };
            _settings.Save();
            UpdateModelBrowserVisibility();
            _ = LoadModelsAsync();
            QueueLmStudioWarmup();
            UpdateStatus("Settings saved ✓");
            SettingsPanel.Visibility = Visibility.Collapsed;
        }

        private string GetSavedModelIdForCurrentProvider()
        {
            return IsLmStudio
                ? _settings.SelectedLmStudioModelId
                : _settings.SelectedOpenRouterModelId;
        }

        private void SaveSelectedModelIdForCurrentProvider(string modelId)
        {
            if (IsLmStudio)
                _settings.SelectedLmStudioModelId = modelId;
            else
                _settings.SelectedOpenRouterModelId = modelId;

            // Keep legacy setting for compatibility with existing installs.
            _settings.SelectedModelId = modelId;
        }

        private void QueueLmStudioWarmup()
        {
            if (!IsLmStudio || _selectedModel == null)
                return;

            var baseUrl = _settings.LmStudioBaseUrl;
            var apiKey = _settings.LmStudioApiKey;
            var modelId = _selectedModel.Id;
            var warmupKey = $"{baseUrl}|{modelId}";
            if (!_warmedLmStudioModels.Add(warmupKey))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await _lmStudioService.WarmupAsync(baseUrl, apiKey, modelId);
                }
                catch
                {
                    // Ignore warmup failures; normal request path still reports errors.
                }
            });
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

                QaList.ItemsSource = _qaItems;

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
                CycleOverlayMode();
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

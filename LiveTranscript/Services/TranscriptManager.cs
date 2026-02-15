using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using LiveTranscript.Models;
using LiveTranscript.Services;

namespace LiveTranscript.Services
{
    /// <summary>
    /// Manages the transcript history. Uses real speaker IDs from Deepgram
    /// or turn-based alternation for providers without diarization.
    /// </summary>
    public class TranscriptManager
    {
        private const int MaxEntries = 200;

        private readonly Dispatcher _dispatcher;
        private readonly ObservableCollection<TranscriptEntry> _entries;

        // Track in-progress turns: key = "{source}:{turnOrder}"
        private readonly System.Collections.Generic.Dictionary<string, TranscriptEntry> _activeTurns = new();

        // For providers without speaker IDs: alternate labels per turn
        private readonly System.Collections.Generic.Dictionary<string, int> _lastSpeakerIndex = new();

        private static readonly string[] FallbackSpeakers = { "Speaker A", "Speaker B", "Speaker C", "Speaker D" };

        public ObservableCollection<TranscriptEntry> Entries => _entries;

        public TranscriptManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _entries = new ObservableCollection<TranscriptEntry>();
        }

        /// <summary>
        /// Processes a normalized TranscriptResult from any provider.
        /// sourceLabel: "mic" or "speaker" — identifies the audio source.
        /// </summary>
        public void ProcessResult(TranscriptResult result, string sourceLabel)
        {
            _dispatcher.Invoke(() =>
            {
                string text = result.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(text)) return;

                string turnKey = $"{sourceLabel}:{result.TurnOrder}";
                string displaySpeaker = ResolveSpeakerLabel(sourceLabel, result);

                if (result.IsFinal)
                {
                    // Final — replace partial or create new
                    if (_activeTurns.TryGetValue(turnKey, out var partial))
                    {
                        partial.Text = text;
                        partial.Speaker = displaySpeaker;
                        partial.IsFinal = true;
                        RefreshEntry(partial);
                        _activeTurns.Remove(turnKey);
                    }
                    else
                    {
                        AddEntry(new TranscriptEntry
                        {
                            Speaker = displaySpeaker,
                            Text = text,
                            IsFinal = true,
                            TurnOrder = result.TurnOrder,
                            Timestamp = DateTime.Now
                        });
                    }
                    return;
                }

                // Interim / partial update
                if (_activeTurns.TryGetValue(turnKey, out var active))
                {
                    active.Text = text;
                    active.Speaker = displaySpeaker;
                    RefreshEntry(active);
                }
                else
                {
                    var entry = new TranscriptEntry
                    {
                        Speaker = displaySpeaker,
                        Text = text,
                        IsFinal = false,
                        TurnOrder = result.TurnOrder,
                        Timestamp = DateTime.Now
                    };
                    _activeTurns[turnKey] = entry;
                    AddEntry(entry);
                }
            });
        }

        private string ResolveSpeakerLabel(string sourceLabel, TranscriptResult result)
        {
            if (sourceLabel == "mic")
                return "🎤 You";

            // If we have a real speaker ID from the provider (e.g. Deepgram)
            if (result.SpeakerId.HasValue)
            {
                int id = result.SpeakerId.Value;
                string letter = id < 26 ? ((char)('A' + id)).ToString() : id.ToString();
                return $"🔊 Speaker {letter}";
            }

            // Fallback for providers without speaker IDs: alternate per turn
            string key = $"{sourceLabel}:fallback";
            if (!_lastSpeakerIndex.TryGetValue(key, out int lastIdx))
                lastIdx = -1;

            int nextIdx = (lastIdx + 1) % FallbackSpeakers.Length;
            _lastSpeakerIndex[key] = nextIdx;
            return $"🔊 {FallbackSpeakers[nextIdx]}";
        }

        private void RefreshEntry(TranscriptEntry entry)
        {
            int idx = _entries.IndexOf(entry);
            if (idx >= 0)
            {
                _entries.RemoveAt(idx);
                _entries.Insert(idx, entry);
            }
        }

        private void AddEntry(TranscriptEntry entry)
        {
            _entries.Add(entry);
            while (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }

        public void Clear()
        {
            _dispatcher.Invoke(() =>
            {
                _entries.Clear();
                _activeTurns.Clear();
                _lastSpeakerIndex.Clear();
            });
        }
    }
}

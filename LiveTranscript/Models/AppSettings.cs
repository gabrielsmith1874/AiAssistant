using System;
using System.IO;
using Newtonsoft.Json;

namespace LiveTranscript.Models
{
    /// <summary>
    /// Persistent app settings (API keys, resume, job description, selected model).
    /// Saved to settings.json next to the executable.
    /// </summary>
    public class AppSettings
    {
        private static readonly string SettingsPath;

        static AppSettings()
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            // Walk up to find the project root (where .env lives)
            var current = new DirectoryInfo(dir);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, ".env")))
                {
                    SettingsPath = Path.Combine(current.FullName, "settings.json");
                    return;
                }
                current = current.Parent;
            }
            SettingsPath = Path.Combine(dir, "settings.json");
        }

        public string OpenRouterApiKey { get; set; } = string.Empty;
        public string ClaudeApiKey { get; set; } = string.Empty;
        public string AssemblyAiApiKey { get; set; } = string.Empty;
        public string DeepgramApiKey { get; set; } = string.Empty;
        public string JobDescription { get; set; } = string.Empty;
        public string Resume { get; set; } = string.Empty;
        public string SelectedModelId { get; set; } = string.Empty;
        public string AiProvider { get; set; } = "OpenRouter";

        // Window Persistence
        public double WindowTop { get; set; } = -1;
        public double WindowLeft { get; set; } = -1;
        public double WindowWidth { get; set; } = 950;
        public double WindowHeight { get; set; } = 600;

        [JsonIgnore]
        private static AppSettings? _instance;

        public static AppSettings Load()
        {
            if (_instance != null) return _instance;

            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    _instance = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
                else
                {
                    _instance = new AppSettings();
                }
            }
            catch
            {
                _instance = new AppSettings();
            }

            return _instance;
        }

        public void Save()
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsPath, json);
            }
            catch { /* Best effort */ }
        }
    }
}

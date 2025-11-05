using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TranscribeMeetingUI
{
    public class AppSettings
    {
        // Transcription settings
        [JsonPropertyName("useAzureSTT")]
        public bool UseAzureSTT { get; set; } = false;

        [JsonPropertyName("whisperExePath")]
        public string WhisperExePath { get; set; } = @"D:\Documents\LocalAI\whisper-cublas-12.4.0-bin-x64\Release\whisper-cli.exe";

        [JsonPropertyName("whisperModelPath")]
        public string WhisperModelPath { get; set; } = @"D:\Documents\LocalAI\whisper-cublas-12.4.0-bin-x64\Release\Models\ggml-small.bin";

        [JsonPropertyName("azureSTTKey")]
        public string AzureSTTKey { get; set; } = "";

        [JsonPropertyName("azureRegion")]
        public string AzureRegion { get; set; } = "eastus";

        // Summary settings
        [JsonPropertyName("useOpenRouter")]
        public bool UseOpenRouter { get; set; } = false;

        [JsonPropertyName("ollamaUrl")]
        public string OllamaUrl { get; set; } = "http://localhost:11434";

        [JsonPropertyName("ollamaModel")]
        public string OllamaModel { get; set; } = "llama3.1:8b";

        [JsonPropertyName("openRouterKey")]
        public string OpenRouterKey { get; set; } = "";

        [JsonPropertyName("openRouterModel")]
        public string OpenRouterModel { get; set; } = "anthropic/claude-3-sonnet";

        // Translation settings
        [JsonPropertyName("enableTranslation")]
        public bool EnableTranslation { get; set; } = false;

        [JsonPropertyName("deepLKey")]
        public string DeepLKey { get; set; } = "";

        [JsonPropertyName("targetLanguage")]
        public string TargetLanguage { get; set; } = "EN-US";

        // Real-time settings
        [JsonPropertyName("realTimeChunkSeconds")]
        public int RealTimeChunkSeconds { get; set; } = 30;

        // General settings
        [JsonPropertyName("enableSummary")]
        public bool EnableSummary { get; set; } = true;

        [JsonPropertyName("autoExport")]
        public bool AutoExport { get; set; } = false;

        [JsonPropertyName("outputDirectory")]
        public string OutputDirectory { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasterTranscriber");

        // Config file path
        private static readonly string ConfigFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasterTranscriber",
            "settings.json"
        );

        public static AppSettings LoadFromFile()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        System.Diagnostics.Debug.WriteLine("[Settings] Loaded from file");
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Error loading: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("[Settings] Using defaults");
            return new AppSettings();
        }

        public void SaveToFile()
        {
            try
            {
                string directory = Path.GetDirectoryName(ConfigFilePath)!;
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(ConfigFilePath, json);

                System.Diagnostics.Debug.WriteLine($"[Settings] Saved to: {ConfigFilePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Error saving: {ex.Message}");
            }
        }

        public AppSettings Clone()
        {
            string json = JsonSerializer.Serialize(this);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
    }
}
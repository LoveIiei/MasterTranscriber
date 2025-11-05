using System.Diagnostics;
using System.IO;

namespace TranscribeMeetingUI
{
    public class Transcriber
    {
        private readonly AppSettings settings;
        private AzureSTTHandler? azureHandler;

        public Transcriber(AppSettings settings)
        {
            this.settings = settings;

            if (settings.UseAzureSTT && !string.IsNullOrEmpty(settings.AzureSTTKey))
            {
                azureHandler = new AzureSTTHandler(settings.AzureSTTKey, settings.AzureRegion);
            }
        }

        public async Task<string> TranscribeAsync(string audioFilePath, IProgress<string>? progress = null)
        {
            if (settings.UseAzureSTT)
            {
                return await TranscribeWithAzureAsync(audioFilePath, progress);
            }
            else
            {
                return await TranscribeWithWhisperAsync(audioFilePath);
            }
        }

        private async Task<string> TranscribeWithAzureAsync(string audioFilePath, IProgress<string>? progress = null)
        {
            if (azureHandler == null)
            {
                throw new Exception("Azure STT is not configured. Please check your API key.");
            }

            progress?.Report("Transcribing with Azure Speech-to-Text...");

            // Check file size to decide which method to use
            var fileInfo = new FileInfo(audioFilePath);
            if (fileInfo.Length > 50 * 1024 * 1024) // > 50MB
            {
                return await azureHandler.TranscribeLongAudioAsync(audioFilePath, progress);
            }
            else
            {
                return await azureHandler.TranscribeAudioFileAsync(audioFilePath);
            }
        }

        private async Task<string> TranscribeWithWhisperAsync(string audioFilePath)
        {
            // Get the directory and filename
            string audioDirectory = Path.GetDirectoryName(audioFilePath) ?? Environment.CurrentDirectory;
            string audioFileNameWithoutExt = Path.GetFileNameWithoutExtension(audioFilePath);

            // Build the FULL output path (without extension, whisper adds .txt)
            string outputPathWithoutExt = Path.Combine(audioDirectory, audioFileNameWithoutExt);
            string transcriptPath = outputPathWithoutExt + ".txt";

            string arguments = $"-m \"{settings.WhisperModelPath}\" -f \"{audioFilePath}\" -of \"{outputPathWithoutExt}\" -otxt";

            System.Diagnostics.Debug.WriteLine($"[Whisper] Command: {settings.WhisperExePath} {arguments}");
            System.Diagnostics.Debug.WriteLine($"[Whisper] Expected transcript: {transcriptPath}");

            var startInfo = new ProcessStartInfo
            {
                FileName = settings.WhisperExePath,
                Arguments = arguments,
                RedirectStandardOutput = false,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start whisper.cpp process.");
                }

                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[Whisper] Error output: {stderr}");
                    throw new Exception($"whisper.cpp failed with exit code {process.ExitCode}:\n{stderr}");
                }
            }

            // Check if transcript was created
            if (!File.Exists(transcriptPath))
            {
                // Try alternate location (current directory)
                string altPath = Path.Combine(Environment.CurrentDirectory, audioFileNameWithoutExt + ".txt");
                System.Diagnostics.Debug.WriteLine($"[Whisper] Primary path not found, checking: {altPath}");

                if (File.Exists(altPath))
                {
                    transcriptPath = altPath;
                }
                else
                {
                    throw new FileNotFoundException($"Whisper did not create transcript file. Expected: {transcriptPath}", transcriptPath);
                }
            }

            System.Diagnostics.Debug.WriteLine($"[Whisper] ✓ Transcript found at: {transcriptPath}");
            string transcript = await File.ReadAllTextAsync(transcriptPath);

            // Clean up the transcript file
            try
            {
                File.Delete(transcriptPath);
            }
            catch { }

            return transcript;
        }
    }
}
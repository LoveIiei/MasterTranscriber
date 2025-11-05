// Program.cs
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Diagnostics;       // For Process
using System.Text;              // For Encoding
using System.Text.Json;         // For JsonSerializer
using System.Text.Json.Serialization; // For JsonPropertyName

// --- Main Program Logic ---
public class Program
{
    // --- ACTION REQUIRED: UPDATE THESE PATHS ---
    private const string WHISPER_EXE_PATH = @"D:\Documents\LocalAI\whisper-cublas-12.4.0-bin-x64\Release\whisper-cli.exe"; // Path to your whisper.cpp main.exe
    private const string WHISPER_MODEL_PATH = @"D:\Documents\LocalAI\whisper-cublas-12.4.0-bin-x64\Release\Models\ggml-small.bin"; // Path to your GGML model
    private const string OLLAMA_URL = "http://localhost:11434"; // Ollama API endpoint
    private const string OLLAMA_MODEL = "llama3.1:8b"; // The Ollama model to use
    private const string OUTPUT_WAV_PATH = @"D:\Documents\TranscribeMeeting\my_recording.wav";

    // ---------------------------------------------

    public static async Task Main(string[] args)
    {
        var recorder = new Recorder();
        var transcriber = new Transcriber(); // Added this
        var summarizer = new Summarizer();   // Added this

        try
        {
            Console.WriteLine($"Recording will be saved to: {Path.GetFullPath(OUTPUT_WAV_PATH)}");
            Console.WriteLine("Press ENTER to start recording...");
            Console.ReadLine();

            recorder.StartRecording(OUTPUT_WAV_PATH);
            Console.WriteLine("✅ Recording started! Press ENTER to stop.");

            // Wait for user to press Enter again
            Console.ReadLine();

            // Stop recording and wait for the file to be finalized
            Console.WriteLine("Stopping recording and saving file...");
            await recorder.StopRecordingAsync();
            Console.WriteLine("✅ Recording stopped. File saved.");

            // --- NEW: Transcribe Step ---
            Console.WriteLine("[2/3] Transcribing audio with whisper.cpp...");
            string transcript = await transcriber.TranscribeAsync(WHISPER_EXE_PATH, WHISPER_MODEL_PATH, OUTPUT_WAV_PATH);

            if (string.IsNullOrWhiteSpace(transcript))
            {
                Console.WriteLine("Transcription failed or produced no text.");
                return;
            }
            Console.WriteLine("[2/3] Transcription complete.");

            // --- NEW: Summarize Step ---
            Console.WriteLine("[3/3] Summarizing transcript with Ollama...");
            string summary = await summarizer.SummarizeAsync(OLLAMA_URL, OLLAMA_MODEL, transcript);
            Console.WriteLine("[3/3] Summarization complete.");

            // --- NEW: Show Results ---
            Console.WriteLine("\n--- 🎙️ MEETING SUMMARY ---");
            Console.WriteLine(summary);
            Console.WriteLine("-----------------------------");

        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nAn error occurred: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            // Clean up the generated text file from whisper.cpp
            if (File.Exists(OUTPUT_WAV_PATH + ".txt"))
            {
                File.Delete(OUTPUT_WAV_PATH + ".txt");
            }
            Console.WriteLine("\nProcess finished. Press any key to exit.");
            Console.ReadKey();
        }
    }
}

public class Recorder
{
    private WasapiLoopbackCapture? systemCapture;
    private WaveInEvent? micCapture;
    private WaveFileWriter? waveWriter;
    private BufferedWaveProvider? systemBuffer;
    private BufferedWaveProvider? micBuffer;
    private Task? mixingTask;
    private CancellationTokenSource? cts;
    private volatile bool isRecording = false;

    public void StartRecording(string outputFilePath)
    {
        cts = new CancellationTokenSource();
        isRecording = true;

        // System audio (loopback)
        systemCapture = new WasapiLoopbackCapture();
        systemBuffer = new BufferedWaveProvider(systemCapture.WaveFormat)
        {
            BufferLength = 10 * 1024 * 1024,
            DiscardOnBufferOverflow = true
        };

        // Microphone - match system format
        micCapture = new WaveInEvent
        {
            WaveFormat = new WaveFormat(
                systemCapture.WaveFormat.SampleRate,
                systemCapture.WaveFormat.BitsPerSample,
                systemCapture.WaveFormat.Channels
            )
        };

        micBuffer = new BufferedWaveProvider(micCapture.WaveFormat)
        {
            BufferLength = 10 * 1024 * 1024,
            DiscardOnBufferOverflow = true
        };

        Console.WriteLine($"System format: {systemCapture.WaveFormat}");
        Console.WriteLine($"Mic format: {micCapture.WaveFormat}");

        // Use system audio format as the output format
        waveWriter = new WaveFileWriter(outputFilePath, systemCapture.WaveFormat);

        // Capture handlers
        systemCapture.DataAvailable += (s, e) => systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        micCapture.DataAvailable += (s, e) => micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

        // Start capturing first
        systemCapture.StartRecording();
        micCapture.StartRecording();

        // Then start mixing task
        mixingTask = Task.Run(() => MixAndWrite(cts.Token));
    }

    private void MixAndWrite(CancellationToken token)
    {
        var systemSample = systemBuffer!.ToSampleProvider();
        var micSample = micBuffer!.ToSampleProvider();

        // Ensure both have the same number of channels
        if (micSample.WaveFormat.Channels == 1 && systemSample.WaveFormat.Channels == 2)
        {
            micSample = micSample.ToStereo();
        }
        else if (micSample.WaveFormat.Channels == 2 && systemSample.WaveFormat.Channels == 1)
        {
            micSample = micSample.ToMono();
        }

        // Handle sample rate differences
        ISampleProvider micAdjusted = micSample;
        if (micSample.WaveFormat.SampleRate != systemSample.WaveFormat.SampleRate)
        {
            micAdjusted = new WdlResamplingSampleProvider(micSample, systemSample.WaveFormat.SampleRate);
        }

        var mixer = new MixingSampleProvider(new[] { systemSample, micAdjusted });

        // Buffer size: 100ms of audio
        int bufferMilliseconds = 100;
        int bufferSamples = (systemCapture!.WaveFormat.SampleRate * systemCapture.WaveFormat.Channels * bufferMilliseconds) / 1000;
        var buffer = new float[bufferSamples];

        while (isRecording && !token.IsCancellationRequested)
        {
            // Only read if there's data available in the buffers
            if (systemBuffer.BufferedBytes > 0 || micBuffer.BufferedBytes > 0)
            {
                int samplesRead = mixer.Read(buffer, 0, buffer.Length);
                if (samplesRead > 0)
                {
                    waveWriter?.WriteSamples(buffer, 0, samplesRead);
                }
            }
            else
            {
                // Wait for data
                Thread.Sleep(10);
            }
        }

        // Final flush - read any remaining data
        while (systemBuffer.BufferedBytes > 0 || micBuffer.BufferedBytes > 0)
        {
            int samplesRead = mixer.Read(buffer, 0, buffer.Length);
            if (samplesRead > 0)
            {
                waveWriter?.WriteSamples(buffer, 0, samplesRead);
            }
            else
            {
                break;
            }
        }

        waveWriter?.Flush();
    }

    public async Task StopRecordingAsync()
    {
        isRecording = false;

        systemCapture?.StopRecording();
        micCapture?.StopRecording();

        // Wait for buffers to finish
        await Task.Delay(200);

        cts?.Cancel();
        if (mixingTask != null)
        {
            await mixingTask;
        }

        waveWriter?.Dispose();
        systemCapture?.Dispose();
        micCapture?.Dispose();

        waveWriter = null;
        systemCapture = null;
        micCapture = null;
    }
}

public class Transcriber
{
    public async Task<string> TranscribeAsync(string whisperExePath, string modelPath, string audioFilePath)
    {
        string audioDirectory = Path.GetDirectoryName(audioFilePath) ?? Environment.CurrentDirectory;
        string audioFileNameWithoutExt = Path.GetFileNameWithoutExtension(audioFilePath);

        // Build the FULL output path (without extension, whisper adds .txt)
        string outputPathWithoutExt = Path.Combine(audioDirectory, audioFileNameWithoutExt);
        string transcriptPath = outputPathWithoutExt + ".txt";

        string arguments = $"-m \"{modelPath}\" -f \"{audioFilePath}\" -of \"{outputPathWithoutExt}\" -otxt";

        var startInfo = new ProcessStartInfo
        {
            FileName = whisperExePath,
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
                throw new Exception($"whisper.cpp failed with exit code {process.ExitCode}:\n{stderr}");
            }
        }

        if (!File.Exists(transcriptPath))
        {
            throw new FileNotFoundException($"whisper.cpp did not create the transcript file at: {transcriptPath}", transcriptPath);
        }

        return await File.ReadAllTextAsync(transcriptPath);
    }
}

// --- NEW: Handles Calling Ollama API ---
public class Summarizer
{
    // Use a static HttpClient for performance
    private static readonly HttpClient httpClient = new HttpClient();

    public async Task<string> SummarizeAsync(string ollamaUrl, string modelName, string transcript)
    {
        string prompt = $"Here is a transcript of a meeting. Please provide a concise summary and a list of key highlights or action items.\n\nTranscript:\n\"\"\"\n{transcript}\n\"\"\"";

        var requestBody = new OllamaChatRequest
        {
            Model = modelName,
            Messages = new[] { new OllamaMessage { Role = "user", Content = prompt } },
            Stream = false
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await httpClient.PostAsync($"{ollamaUrl}/api/chat", content);

        try
        {
            response.EnsureSuccessStatusCode(); // Throws an exception if the API call failed
        }
        catch (HttpRequestException e)
        {
            // Provide more context on failure
            throw new Exception($"Failed to call Ollama API. Is Ollama running at {ollamaUrl}? Error: {e.Message}");
        }

        var responseString = await response.Content.ReadAsStringAsync();
        var ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseString);

        return ollamaResponse?.Message?.Content ?? "No summary content received.";
    }

    // --- C# Models for Ollama JSON ---
    private class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";
        [JsonPropertyName("messages")]
        public OllamaMessage[] Messages { get; set; } = Array.Empty<OllamaMessage>();
        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private class OllamaMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";
        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }

    private class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }
    }
}
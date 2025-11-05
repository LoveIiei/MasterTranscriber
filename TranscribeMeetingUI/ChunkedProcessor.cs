using NAudio.Wave;
using System.IO;
using TranscribeMeetingUI;

public class ChunkedProcessor
{
    private const int CHUNK_DURATION_SECONDS = 600; // 10 minutes

    // NEW: Separate method for transcription only
    public async Task<TranscriptionResult> TranscribeAudioAsync(
    string audioFilePath,
    Transcriber transcriber,
    IProgress<string>? progress = null)
    {
        progress?.Report("Analyzing audio file...");

        var audioInfo = GetAudioInfo(audioFilePath);

        System.Diagnostics.Debug.WriteLine($"[DEBUG] Audio file: {audioFilePath}");
        System.Diagnostics.Debug.WriteLine($"[DEBUG] File size: {new FileInfo(audioFilePath).Length / (1024.0 * 1024.0):F2} MB");
        System.Diagnostics.Debug.WriteLine($"[DEBUG] Duration: {audioInfo.DurationSeconds} seconds ({audioInfo.DurationSeconds / 60.0:F2} minutes)");

        bool needsChunking = audioInfo.DurationSeconds > CHUNK_DURATION_SECONDS;

        if (!needsChunking)
        {
            progress?.Report("Transcribing audio...");
            string transcript = await transcriber.TranscribeAsync(audioFilePath, progress);

            return new TranscriptionResult
            {
                FullTranscript = transcript,
                ChunkCount = 1
            };
        }

        progress?.Report($"Long recording detected ({FormatDuration(audioInfo.DurationSeconds)}). Processing in chunks...");

        var chunks = await SplitAudioIntoChunksAsync(audioFilePath, CHUNK_DURATION_SECONDS);
        var chunkResults = new List<ChunkResult>();

        for (int i = 0; i < chunks.Count; i++)
        {
            progress?.Report($"Transcribing chunk {i + 1}/{chunks.Count} ({FormatDuration(chunks[i].StartSeconds)} - {FormatDuration(chunks[i].EndSeconds)})");

            string chunkTranscript = await transcriber.TranscribeAsync(chunks[i].FilePath, progress);

            chunkResults.Add(new ChunkResult
            {
                ChunkNumber = i + 1,
                StartTime = FormatDuration(chunks[i].StartSeconds),
                EndTime = FormatDuration(chunks[i].EndSeconds),
                Transcript = chunkTranscript,
                Summary = ""
            });

            try { File.Delete(chunks[i].FilePath); } catch { }
            try { File.Delete(chunks[i].FilePath + ".txt"); } catch { }
        }

        string fullTranscript = string.Join("\n\n" + new string('=', 80) + "\n\n",
            chunkResults.Select(c => $"[{c.StartTime} - {c.EndTime}]\n{c.Transcript}"));

        return new TranscriptionResult
        {
            FullTranscript = fullTranscript,
            ChunkCount = chunkResults.Count,
            ChunkResults = chunkResults
        };
    }

    // NEW: Separate method for summarization
    public async Task<string> SummarizeTranscriptAsync(
    TranscriptionResult transcriptionResult,
    Summarizer summarizer,
    IProgress<string>? progress = null)  // REMOVED: ollamaUrl, ollamaModel
    {
        if (transcriptionResult.ChunkCount == 1 || transcriptionResult.ChunkResults == null)
        {
            progress?.Report("Generating AI summary...");
            return await summarizer.SummarizeAsync(transcriptionResult.FullTranscript);
        }

        progress?.Report("Summarizing individual segments...");

        for (int i = 0; i < transcriptionResult.ChunkResults.Count; i++)
        {
            progress?.Report($"Summarizing segment {i + 1}/{transcriptionResult.ChunkResults.Count}...");

            var chunk = transcriptionResult.ChunkResults[i];
            chunk.Summary = await summarizer.SummarizeChunkAsync(chunk.Transcript, chunk.ChunkNumber);
        }

        progress?.Report("Creating final summary from all segments...");

        string combinedSummaries = string.Join("\n\n", transcriptionResult.ChunkResults.Select(c =>
            $"Segment {c.ChunkNumber} ({c.StartTime} - {c.EndTime}):\n{c.Summary}"));

        string finalSummary = await summarizer.SummarizeCombinedChunksAsync(
            combinedSummaries, transcriptionResult.ChunkResults.Count);

        return finalSummary;
    }

    // OLD: Keep for backward compatibility (now calls the two methods above)
    public async Task<ProcessingResult> ProcessLongRecordingAsync(
    string audioFilePath,
    Transcriber transcriber,
    Summarizer summarizer,
    IProgress<string>? progress = null)  // REMOVED all the path/url parameters
    {
        var transcriptionResult = await TranscribeAudioAsync(audioFilePath, transcriber, progress);
        string summary = await SummarizeTranscriptAsync(transcriptionResult, summarizer, progress);

        return new ProcessingResult
        {
            FullTranscript = transcriptionResult.FullTranscript,
            FinalSummary = summary,
            ChunkCount = transcriptionResult.ChunkCount,
            ChunkResults = transcriptionResult.ChunkResults
        };
    }

    private AudioInfo GetAudioInfo(string audioFilePath)
    {
        using (var reader = new AudioFileReader(audioFilePath))
        {
            return new AudioInfo
            {
                DurationSeconds = (int)reader.TotalTime.TotalSeconds,
                SampleRate = reader.WaveFormat.SampleRate,
                Channels = reader.WaveFormat.Channels
            };
        }
    }

    private async Task<List<AudioChunk>> SplitAudioIntoChunksAsync(string audioFilePath, int chunkDurationSeconds)
    {
        var chunks = new List<AudioChunk>();

        using (var reader = new AudioFileReader(audioFilePath))
        {
            int totalSeconds = (int)reader.TotalTime.TotalSeconds;
            int chunkCount = (int)Math.Ceiling((double)totalSeconds / chunkDurationSeconds);
            string directory = Path.GetDirectoryName(audioFilePath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(audioFilePath);

            for (int i = 0; i < chunkCount; i++)
            {
                int startSeconds = i * chunkDurationSeconds;
                int endSeconds = Math.Min((i + 1) * chunkDurationSeconds, totalSeconds);
                int durationSeconds = endSeconds - startSeconds;

                string chunkPath = Path.Combine(directory, $"{baseName}_chunk_{i + 1}.wav");

                reader.Position = 0;
                reader.CurrentTime = TimeSpan.FromSeconds(startSeconds);

                using (var writer = new WaveFileWriter(chunkPath, reader.WaveFormat))
                {
                    byte[] buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
                    int bytesToRead = durationSeconds * reader.WaveFormat.AverageBytesPerSecond;
                    int bytesRead = 0;

                    while (bytesRead < bytesToRead)
                    {
                        int toRead = Math.Min(buffer.Length, bytesToRead - bytesRead);
                        int read = reader.Read(buffer, 0, toRead);
                        if (read == 0) break;

                        writer.Write(buffer, 0, read);
                        bytesRead += read;
                    }
                }

                chunks.Add(new AudioChunk
                {
                    FilePath = chunkPath,
                    StartSeconds = startSeconds,
                    EndSeconds = endSeconds,
                    ChunkNumber = i + 1
                });
            }
        }

        return chunks;
    }

    private string FormatDuration(int totalSeconds)
    {
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;

        if (hours > 0)
            return $"{hours}:{minutes:D2}:{seconds:D2}";
        else
            return $"{minutes}:{seconds:D2}";
    }

    private class AudioInfo
    {
        public int DurationSeconds { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
    }

    private class AudioChunk
    {
        public string FilePath { get; set; } = "";
        public int StartSeconds { get; set; }
        public int EndSeconds { get; set; }
        public int ChunkNumber { get; set; }
    }
}

// NEW: Separate result class for transcription
public class TranscriptionResult
{
    public string FullTranscript { get; set; } = "";
    public int ChunkCount { get; set; }
    public List<ChunkResult>? ChunkResults { get; set; }
}

public class ProcessingResult
{
    public string FullTranscript { get; set; } = "";
    public string FinalSummary { get; set; } = "";
    public int ChunkCount { get; set; }
    public List<ChunkResult>? ChunkResults { get; set; }
}

public class ChunkResult
{
    public int ChunkNumber { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string Transcript { get; set; } = "";
    public string Summary { get; set; } = "";
}
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Collections.Concurrent;
using System.IO;


namespace TranscribeMeetingUI
{
    public class RealTimeTranscriber
    {
        private int chunkDurationSeconds = 30; // Process every 30 seconds

        private WasapiLoopbackCapture? systemCapture;
        private WaveInEvent? micCapture;
        private WaveFileWriter? mainFileWriter;
        private WaveFileWriter? chunkWriter;
        private BufferedWaveProvider? systemBuffer;
        private BufferedWaveProvider? micBuffer;
        private Task? mixingTask;
        private Task? processingTask;
        private CancellationTokenSource? cts;
        private volatile bool isRecording = false;
        private bool microphoneEnabled = true;
        private string outputDirectory = "";
        private string baseFileName = "";
        private int chunkCounter = 0;
        private DateTime lastChunkTime;
        private DateTime recordingStartTime;

        private ConcurrentQueue<ChunkToProcess> chunksToProcess = new ConcurrentQueue<ChunkToProcess>();
        private List<TranscriptSegment> transcriptSegments = new List<TranscriptSegment>();

        public event EventHandler<TranscriptUpdateEventArgs>? TranscriptUpdated;
        public event EventHandler<string>? StatusUpdated;

        private Dictionary<int, string> translatedSegments = new Dictionary<int, string>();
        private DeepLTranslator? translator;

        public void StartRecording(string outputFilePath, bool enableMicrophone = true,
            DeepLTranslator? translator = null, int chunkIntervalSeconds = 30)
        {
            cts = new CancellationTokenSource();
            isRecording = true;
            microphoneEnabled = enableMicrophone;
            recordingStartTime = DateTime.Now;
            lastChunkTime = DateTime.Now;
            chunkCounter = 0;
            transcriptSegments.Clear();
            translatedSegments.Clear();
            this.translator = translator;
            this.chunkDurationSeconds = chunkIntervalSeconds;

            outputDirectory = Path.GetDirectoryName(outputFilePath) ?? "";
            baseFileName = Path.GetFileNameWithoutExtension(outputFilePath);

            // System audio (loopback)
            systemCapture = new WasapiLoopbackCapture();
            systemBuffer = new BufferedWaveProvider(systemCapture.WaveFormat)
            {
                BufferLength = 10 * 1024 * 1024,
                DiscardOnBufferOverflow = true
            };

            // Main file writer for complete recording
            mainFileWriter = new WaveFileWriter(outputFilePath, systemCapture.WaveFormat);

            // Capture system audio
            systemCapture.DataAvailable += (s, e) => systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            // Setup microphone if enabled
            if (microphoneEnabled)
            {
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

                micCapture.DataAvailable += (s, e) => micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            }

            // Start capturing
            systemCapture.StartRecording();
            if (microphoneEnabled && micCapture != null)
            {
                micCapture.StartRecording();
            }

            // Start mixing and processing tasks
            mixingTask = Task.Run(() => MixAndWrite(cts.Token));
            processingTask = Task.Run(() => ProcessChunksAsync(cts.Token));
        }

        private void MixAndWrite(CancellationToken token)
        {
            var systemSample = systemBuffer!.ToSampleProvider();
            ISampleProvider finalProvider;

            if (microphoneEnabled && micBuffer != null)
            {
                var micSample = micBuffer.ToSampleProvider();

                if (micSample.WaveFormat.Channels == 1 && systemSample.WaveFormat.Channels == 2)
                {
                    micSample = micSample.ToStereo();
                }
                else if (micSample.WaveFormat.Channels == 2 && systemSample.WaveFormat.Channels == 1)
                {
                    micSample = micSample.ToMono();
                }

                ISampleProvider micAdjusted = micSample;
                if (micSample.WaveFormat.SampleRate != systemSample.WaveFormat.SampleRate)
                {
                    micAdjusted = new WdlResamplingSampleProvider(micSample, systemSample.WaveFormat.SampleRate);
                }

                var mixer = new MixingSampleProvider(new[] { systemSample, micAdjusted });
                finalProvider = mixer;
            }
            else
            {
                finalProvider = systemSample;
            }

            int bufferMilliseconds = 100;
            int bufferSamples = (systemCapture!.WaveFormat.SampleRate * systemCapture.WaveFormat.Channels * bufferMilliseconds) / 1000;
            var buffer = new float[bufferSamples];

            // Create first chunk writer
            string chunkPath = GetChunkPath(chunkCounter);
            chunkWriter = new WaveFileWriter(chunkPath, systemCapture.WaveFormat);

            while (isRecording && !token.IsCancellationRequested)
            {
                bool hasSystemData = systemBuffer.BufferedBytes > 0;
                bool hasMicData = microphoneEnabled && micBuffer != null && micBuffer.BufferedBytes > 0;

                if (hasSystemData || hasMicData)
                {
                    int samplesRead = finalProvider.Read(buffer, 0, buffer.Length);
                    if (samplesRead > 0)
                    {
                        // Write to main file
                        mainFileWriter?.WriteSamples(buffer, 0, samplesRead);

                        // Write to chunk file
                        chunkWriter?.WriteSamples(buffer, 0, samplesRead);

                        // Check if we need to start a new chunk
                        if ((DateTime.Now - lastChunkTime).TotalSeconds >= chunkDurationSeconds)
                        {
                            // Finalize current chunk
                            chunkWriter?.Flush();
                            chunkWriter?.Dispose();

                            var elapsedFromStart = DateTime.Now - recordingStartTime;
                            var chunkStartTime = elapsedFromStart.TotalSeconds - chunkDurationSeconds;

                            // Queue chunk for processing
                            chunksToProcess.Enqueue(new ChunkToProcess
                            {
                                FilePath = chunkPath,
                                ChunkNumber = chunkCounter,
                                StartTime = TimeSpan.FromSeconds(chunkStartTime),
                                EndTime = elapsedFromStart
                            });

                            // Start new chunk
                            chunkCounter++;
                            chunkPath = GetChunkPath(chunkCounter);
                            chunkWriter = new WaveFileWriter(chunkPath, systemCapture.WaveFormat);
                            lastChunkTime = DateTime.Now;
                        }
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }

            // Finalize last chunk
            chunkWriter?.Flush();
            chunkWriter?.Dispose();

            // Queue final chunk if it has content
            var finalElapsed = DateTime.Now - recordingStartTime;
            var finalChunkStart = chunkCounter > 0 ? finalElapsed.TotalSeconds - (DateTime.Now - lastChunkTime).TotalSeconds : 0;

            if (File.Exists(chunkPath) && new FileInfo(chunkPath).Length > 1000) // At least 1KB
            {
                chunksToProcess.Enqueue(new ChunkToProcess
                {
                    FilePath = chunkPath,
                    ChunkNumber = chunkCounter,
                    StartTime = TimeSpan.FromSeconds(finalChunkStart),
                    EndTime = finalElapsed
                });
            }

            // Flush remaining data to main file
            while (systemBuffer.BufferedBytes > 0 || (microphoneEnabled && micBuffer != null && micBuffer.BufferedBytes > 0))
            {
                int samplesRead = finalProvider.Read(buffer, 0, buffer.Length);
                if (samplesRead > 0)
                {
                    mainFileWriter?.WriteSamples(buffer, 0, samplesRead);
                }
                else
                {
                    break;
                }
            }

            mainFileWriter?.Flush();
        }

        private async Task ProcessChunksAsync(CancellationToken token)
        {
            await Task.Delay(2000, token);

            while (!token.IsCancellationRequested)
            {
                if (chunksToProcess.TryDequeue(out var chunk))
                {
                    try
                    {
                        StatusUpdated?.Invoke(this, $"Transcribing segment {chunk.ChunkNumber + 1}...");
                        OnChunkReadyForProcessing(chunk);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Error processing chunk {chunk.ChunkNumber}: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        await Task.Delay(500, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Normal cancellation
                    }
                }
            }

            // Process remaining chunks even after cancellation
            System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Processing {chunksToProcess.Count} remaining chunks...");
            while (chunksToProcess.TryDequeue(out var chunk))
            {
                try
                {
                    OnChunkReadyForProcessing(chunk);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Error processing remaining chunk: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("[DEBUG RT] All chunks processed");
        }

        public event EventHandler<ChunkToProcess>? ChunkReadyForProcessing;

        private void OnChunkReadyForProcessing(ChunkToProcess chunk)
        {
            ChunkReadyForProcessing?.Invoke(this, chunk);
        }

        public void AddTranscriptSegment(int chunkNumber, TimeSpan startTime, TimeSpan endTime, string transcript)
        {
            var segment = new TranscriptSegment
            {
                ChunkNumber = chunkNumber,
                StartTime = startTime,
                EndTime = endTime,
                Transcript = transcript
            };

            transcriptSegments.Add(segment);
            if (translator != null)
            {
                _ = TranslateSegmentAsync(chunkNumber, transcript);
            }

            TranscriptUpdated?.Invoke(this, new TranscriptUpdateEventArgs
            {
                Segment = segment,
                AllSegments = new List<TranscriptSegment>(transcriptSegments)
            });
        }

        private async Task TranslateSegmentAsync(int chunkNumber, string transcript)
        {
            try
            {
                if (translator != null)
                {
                    string translated = await translator.TranslateAsync(transcript);
                    translatedSegments[chunkNumber] = translated;
                    System.Diagnostics.Debug.WriteLine($"[RT Translation] Segment {chunkNumber} translated");
                }
                var segment = transcriptSegments.FirstOrDefault(s => s.ChunkNumber == chunkNumber);
                if (segment != null)
                {
                    TranscriptUpdated?.Invoke(this, new TranscriptUpdateEventArgs
                    {
                        Segment = segment,
                        AllSegments = new List<TranscriptSegment>(transcriptSegments)
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RT Translation] Error: {ex.Message}");
            }
        }


        public async Task<string> StopRecordingAsync()
        {
            var recordingEndTime = DateTime.Now;
            var actualRecordingDuration = recordingEndTime - recordingStartTime;

            System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Stop requested at: {recordingEndTime:HH:mm:ss}");
            System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Recording duration: {actualRecordingDuration.TotalSeconds:F2} seconds");

            isRecording = false;

            systemCapture?.StopRecording();
            micCapture?.StopRecording();

            await Task.Delay(200);

            // Finalize current chunk if it exists and has content
            if (chunkWriter != null)
            {
                try
                {
                    chunkWriter.Flush();
                    chunkWriter.Dispose();
                    chunkWriter = null;

                    string currentChunkPath = GetChunkPath(chunkCounter);
                    if (File.Exists(currentChunkPath))
                    {
                        var fileInfo = new FileInfo(currentChunkPath);
                        // Only queue if file has meaningful content (> 100KB)
                        if (fileInfo.Length > 100000)
                        {
                            var elapsedFromStart = recordingEndTime - recordingStartTime;
                            var chunkStartSeconds = (recordingEndTime - lastChunkTime).TotalSeconds;

                            System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Queueing final partial chunk: {currentChunkPath} ({fileInfo.Length / 1024}KB)");

                            chunksToProcess.Enqueue(new ChunkToProcess
                            {
                                FilePath = currentChunkPath,
                                ChunkNumber = chunkCounter,
                                StartTime = TimeSpan.FromSeconds(elapsedFromStart.TotalSeconds - chunkStartSeconds),
                                EndTime = elapsedFromStart
                            });
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Skipping final chunk (too small: {fileInfo.Length / 1024}KB)");
                            File.Delete(currentChunkPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Error finalizing chunk: {ex.Message}");
                }
            }

            // Cancel processing task gracefully
            cts?.Cancel();

            // Wait for processing task to finish with timeout
            if (processingTask != null)
            {
                try
                {
                    await Task.WhenAny(processingTask, Task.Delay(5000)); // 5 second timeout
                }
                catch (OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine("[DEBUG RT] Processing task cancelled (expected)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Error waiting for processing: {ex.Message}");
                }
            }

            if (mixingTask != null)
            {
                try
                {
                    await mixingTask;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Error in mixing task: {ex.Message}");
                }
            }

            mainFileWriter?.Dispose();
            systemCapture?.Dispose();
            micCapture?.Dispose();

            string mainFilePath = Path.Combine(outputDirectory, $"{baseFileName}.wav");

            mainFileWriter = null;
            systemCapture = null;
            micCapture = null;

            System.Diagnostics.Debug.WriteLine($"[DEBUG RT] Recording stopped cleanly");

            return mainFilePath;
        }

        public string GetFullTranscript()
        {
            var sortedSegments = transcriptSegments.OrderBy(s => s.StartTime).ToList();
            var sb = new System.Text.StringBuilder();

            foreach (var segment in sortedSegments)
            {
                sb.AppendLine($"[{FormatTimeSpan(segment.StartTime)} - {FormatTimeSpan(segment.EndTime)}]");
                sb.AppendLine(segment.Transcript);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public string GetTranslatedSegment(int chunkNumber)
        {
            if (translatedSegments.TryGetValue(chunkNumber, out string? translated))
            {
                return translated;
            }
            return "";
        }

        public List<TranscriptSegment> GetAllSegments()
        {
            return new List<TranscriptSegment>(transcriptSegments);
        }

        public string GetTranslatedTranscript()
        {
            var sortedSegments = transcriptSegments.OrderBy(s => s.StartTime).ToList();
            var sb = new System.Text.StringBuilder();

            foreach (var segment in sortedSegments)
            {
                sb.AppendLine($"[{FormatTimeSpan(segment.StartTime)} - {FormatTimeSpan(segment.EndTime)}]");

                // Use translated version if available, otherwise original
                if (translatedSegments.TryGetValue(segment.ChunkNumber, out string? translated))
                {
                    sb.AppendLine(translated);
                }
                else
                {
                    sb.AppendLine(segment.Transcript + " [Translation pending...]");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public void CleanupChunkFiles()
        {
            for (int i = 0; i <= chunkCounter; i++)
            {
                try
                {
                    string chunkPath = GetChunkPath(i);
                    if (File.Exists(chunkPath))
                        File.Delete(chunkPath);
                    if (File.Exists(chunkPath + ".txt"))
                        File.Delete(chunkPath + ".txt");
                }
                catch { }
            }
        }

        private string GetChunkPath(int chunkNum)
        {
            return Path.Combine(outputDirectory, $"{baseFileName}_rt_chunk_{chunkNum}.wav");
        }

        private string FormatTimeSpan(TimeSpan ts)
        {
            return ts.Hours > 0
                ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }
    }

    public class ChunkToProcess
    {
        public string FilePath { get; set; } = "";
        public int ChunkNumber { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
    }

    public class TranscriptSegment
    {
        public int ChunkNumber { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public string Transcript { get; set; } = "";
    }

    public class TranscriptUpdateEventArgs : EventArgs
    {
        public TranscriptSegment Segment { get; set; } = new TranscriptSegment();
        public List<TranscriptSegment> AllSegments { get; set; } = new List<TranscriptSegment>();
    }
}

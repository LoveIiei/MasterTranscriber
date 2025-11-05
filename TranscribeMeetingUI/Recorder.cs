using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public class Recorder
{
    private WasapiLoopbackCapture? systemCapture;
    private WaveInEvent? micCapture;
    private WaveFileWriter? waveWriter;
    private volatile bool isRecording = false;
    private bool microphoneEnabled = true;
    private TaskCompletionSource<bool>? recordingStoppedTcs;
    private object lockObj = new object();
    private long totalBytesWritten = 0;
    private DateTime recordingStartTime;
    private int dataAvailableCallCount = 0;
    private long totalSamplesProcessed = 0;

    public void StartRecording(string outputFilePath, bool enableMicrophone = true)
    {
        isRecording = true;
        microphoneEnabled = enableMicrophone;
        recordingStoppedTcs = new TaskCompletionSource<bool>();
        totalBytesWritten = 0;
        recordingStartTime = DateTime.Now;
        dataAvailableCallCount = 0;
        totalSamplesProcessed = 0;

        System.Diagnostics.Debug.WriteLine($"[DEBUG] ========== RECORDING START ==========");
        System.Diagnostics.Debug.WriteLine($"[DEBUG] Microphone enabled: {microphoneEnabled}");
        System.Diagnostics.Debug.WriteLine($"[DEBUG] Output file: {outputFilePath}");

        // System audio (loopback) - always IEEE Float
        systemCapture = new WasapiLoopbackCapture();

        System.Diagnostics.Debug.WriteLine($"[DEBUG] System capture format: {systemCapture.WaveFormat}");
        System.Diagnostics.Debug.WriteLine($"[DEBUG] Recording started at: {recordingStartTime:HH:mm:ss}");

        if (microphoneEnabled)
        {
            micCapture = new WaveInEvent
            {
                WaveFormat = new WaveFormat(
                    systemCapture.WaveFormat.SampleRate,
                    16, // 16-bit PCM (not float!)
                    systemCapture.WaveFormat.Channels
                )
            };

            System.Diagnostics.Debug.WriteLine($"[DEBUG] Mic capture format: {micCapture.WaveFormat}");
        }

        // Output file uses system format (IEEE Float)
        waveWriter = new WaveFileWriter(outputFilePath, systemCapture.WaveFormat);

        if (microphoneEnabled && micCapture != null)
        {
            // Both enabled - need to mix
            var systemBuffer = new BufferedWaveProvider(systemCapture.WaveFormat)
            {
                BufferLength = 5 * 1024 * 1024,
                DiscardOnBufferOverflow = true
            };
            var micBuffer = new BufferedWaveProvider(micCapture.WaveFormat)
            {
                BufferLength = 5 * 1024 * 1024,
                DiscardOnBufferOverflow = true
            };

            systemCapture.DataAvailable += (s, e) =>
            {
                systemBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            micCapture.DataAvailable += (s, e) =>
            {
                micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            // Start both captures
            systemCapture.StartRecording();
            micCapture.StartRecording();

            System.Diagnostics.Debug.WriteLine("[DEBUG] Recording in mixed mode");

            // Background mixing task
            Task.Run(() => MixStreams(systemBuffer, micBuffer, systemCapture.WaveFormat));
        }
        else
        {
            // System audio only - direct write
            systemCapture.DataAvailable += (s, e) =>
            {
                dataAvailableCallCount++;
                lock (lockObj)
                {
                    if (isRecording && waveWriter != null)
                    {
                        waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
                        waveWriter.Flush();
                        totalBytesWritten += e.BytesRecorded;

                        if (dataAvailableCallCount % 100 == 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG] DataAvailable call #{dataAvailableCallCount}, Total bytes: {totalBytesWritten:N0}");
                        }
                    }
                }
            };

            systemCapture.RecordingStopped += (s, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] RecordingStopped event fired. Total DataAvailable calls: {dataAvailableCallCount}");
                recordingStoppedTcs?.TrySetResult(true);
            };

            systemCapture.StartRecording();

            System.Diagnostics.Debug.WriteLine("[DEBUG] Recording in system-only mode");
        }
    }

    private void MixStreams(BufferedWaveProvider systemBuffer, BufferedWaveProvider micBuffer, WaveFormat outputFormat)
    {
        var systemSample = systemBuffer.ToSampleProvider();
        var mic16Provider = new Wave16ToFloatProvider(micBuffer);
        var micSample = mic16Provider.ToSampleProvider();

        if (micSample.WaveFormat.Channels != systemSample.WaveFormat.Channels)
        {
            if (micSample.WaveFormat.Channels == 1)
                micSample = new MonoToStereoSampleProvider(micSample);
        }

        int bufferSamples = outputFormat.SampleRate * outputFormat.Channels / 10; // 100ms
        var systemSampleBuffer = new float[bufferSamples];
        var micSampleBuffer = new float[bufferSamples];
        var mixedBuffer = new float[bufferSamples];
        var byteBuffer = new byte[bufferSamples * 4];

        System.Diagnostics.Debug.WriteLine($"[DEBUG Mix] Manual mixing - buffer size: {bufferSamples} samples");

        int mixLoopCount = 0;
        long totalSamplesWritten = 0;
        var startTime = DateTime.Now;

        while (isRecording)
        {
            // Calculate expected samples based on elapsed time
            var elapsed = DateTime.Now - startTime;
            long expectedSamples = (long)(elapsed.TotalSeconds * outputFormat.SampleRate * outputFormat.Channels);

            // Only process if we haven't written enough samples yet (throttle based on time!)
            if (totalSamplesWritten < expectedSamples)
            {
                // Check if we have enough data in at least one buffer
                int minBufferBytes = bufferSamples * 2; // Minimum for 16-bit
                bool hasData = systemBuffer.BufferedBytes >= minBufferBytes || micBuffer.BufferedBytes >= minBufferBytes;

                if (hasData)
                {
                    int systemSamplesRead = systemSample.Read(systemSampleBuffer, 0, bufferSamples);
                    int micSamplesRead = micSample.Read(micSampleBuffer, 0, bufferSamples);

                    int samplesToWrite = Math.Max(systemSamplesRead, micSamplesRead);

                    if (samplesToWrite > 0)
                    {
                        for (int i = 0; i < samplesToWrite; i++)
                        {
                            float systemValue = i < systemSamplesRead ? systemSampleBuffer[i] : 0f;
                            float micValue = i < micSamplesRead ? micSampleBuffer[i] : 0f;
                            mixedBuffer[i] = (systemValue + micValue) * 0.5f;
                        }

                        mixLoopCount++;
                        totalSamplesWritten += samplesToWrite;

                        int bytesToWrite = samplesToWrite * 4;
                        Buffer.BlockCopy(mixedBuffer, 0, byteBuffer, 0, bytesToWrite);

                        lock (lockObj)
                        {
                            if (waveWriter != null)
                            {
                                waveWriter.Write(byteBuffer, 0, bytesToWrite);
                                totalBytesWritten += bytesToWrite;
                                totalSamplesProcessed += samplesToWrite;
                            }
                        }

                        if (mixLoopCount % 100 == 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[DEBUG Mix] Iteration #{mixLoopCount}, Written: {totalSamplesWritten:N0}, Expected: {expectedSamples:N0}, Bytes: {totalBytesWritten:N0}");
                        }
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            else
            {
                // We're ahead of real-time, sleep longer
                Thread.Sleep(50);
            }
        }

        System.Diagnostics.Debug.WriteLine($"[DEBUG] Manual mixing ended. Total iterations: {mixLoopCount}, Total samples: {totalSamplesWritten:N0}");

        recordingStoppedTcs?.TrySetResult(true);
    }

    public async Task StopRecordingAsync()
    {
        var recordingEndTime = DateTime.Now;
        var actualRecordingDuration = recordingEndTime - recordingStartTime;

        System.Diagnostics.Debug.WriteLine($"[DEBUG] Stop requested at: {recordingEndTime:HH:mm:ss}");
        System.Diagnostics.Debug.WriteLine($"[DEBUG] Actual recording duration: {actualRecordingDuration.TotalSeconds:F2} seconds ({actualRecordingDuration.TotalMinutes:F2} minutes)");

        isRecording = false;

        systemCapture?.StopRecording();
        micCapture?.StopRecording();

        if (recordingStoppedTcs != null)
        {
            await recordingStoppedTcs.Task;
        }
        else
        {
            await Task.Delay(200);
        }

        lock (lockObj)
        {
            waveWriter?.Flush();
            waveWriter?.Dispose();
            waveWriter = null;
        }

        systemCapture?.Dispose();
        micCapture?.Dispose();
        systemCapture = null;
        micCapture = null;

        // Calculate stats
        if (totalBytesWritten > 0)
        {
            var format = new WaveFormat(48000, 32, 2);
            double calculatedDuration = (double)totalBytesWritten / format.AverageBytesPerSecond;
            double discrepancy = calculatedDuration / actualRecordingDuration.TotalSeconds;

            double expectedBytes = actualRecordingDuration.TotalSeconds * format.AverageBytesPerSecond;
            double expectedSamples = expectedBytes / 4;

            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] ========== FINAL STATS ==========");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] Total bytes written: {totalBytesWritten:N0} ({totalBytesWritten / (1024.0 * 1024.0):F2} MB)");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] Expected bytes: {expectedBytes:N0} ({expectedBytes / (1024.0 * 1024.0):F2} MB)");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] Total samples processed: {totalSamplesProcessed:N0}");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] Expected samples: {expectedSamples:N0}");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] Total DataAvailable calls: {dataAvailableCallCount}");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] File-calculated duration: {calculatedDuration:F2} seconds ({calculatedDuration / 60.0:F2} minutes)");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] Actual recording time: {actualRecordingDuration.TotalSeconds:F2} seconds ({actualRecordingDuration.TotalMinutes:F2} minutes)");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] Discrepancy ratio: {discrepancy:F2}x");
            System.Diagnostics.Debug.WriteLine($"[DEBUG Recorder] =====================================");

            if (discrepancy > 1.2)
            {
                System.Diagnostics.Debug.WriteLine($"[WARNING] File duration is {discrepancy:F2}x longer than actual recording!");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SUCCESS] Duration is accurate!");
            }
        }
    }

    public List<string> GetChunkFiles() => new List<string>();
}
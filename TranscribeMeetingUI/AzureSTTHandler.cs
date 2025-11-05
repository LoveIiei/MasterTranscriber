using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace TranscribeMeetingUI
{
    public class AzureSTTHandler
    {
        private readonly string apiKey;
        private readonly string region;

        public AzureSTTHandler(string apiKey, string region)
        {
            this.apiKey = apiKey;
            this.region = region;
        }

        public async Task<string> TranscribeAudioFileAsync(string audioFilePath)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Azure STT] Transcribing: {audioFilePath}");

                var speechConfig = SpeechConfig.FromSubscription(apiKey, region);
                speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "5000");

                // Azure STT works best with WAV files
                using (var audioConfig = AudioConfig.FromWavFileInput(audioFilePath))
                using (var recognizer = new SpeechRecognizer(speechConfig, audioConfig))
                {
                    var result = await recognizer.RecognizeOnceAsync();

                    if (result.Reason == ResultReason.RecognizedSpeech)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azure STT] Success: {result.Text.Length} chars");
                        return result.Text;
                    }
                    else if (result.Reason == ResultReason.NoMatch)
                    {
                        var noMatchDetails = NoMatchDetails.FromResult(result);
                        System.Diagnostics.Debug.WriteLine($"[Azure STT] No match: {noMatchDetails.Reason}");
                        throw new Exception($"Azure STT could not recognize speech: {noMatchDetails.Reason}");
                    }
                    else if (result.Reason == ResultReason.Canceled)
                    {
                        var cancellation = CancellationDetails.FromResult(result);
                        System.Diagnostics.Debug.WriteLine($"[Azure STT] Canceled: {cancellation.Reason}");

                        if (cancellation.Reason == CancellationReason.Error)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Azure STT] Error: {cancellation.ErrorCode} - {cancellation.ErrorDetails}");
                            throw new Exception($"Azure STT error: {cancellation.ErrorCode} - {cancellation.ErrorDetails}");
                        }

                        throw new Exception($"Azure STT canceled: {cancellation.Reason}");
                    }

                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Azure STT] Exception: {ex.Message}");
                throw;
            }
        }

        public async Task<string> TranscribeLongAudioAsync(string audioFilePath, IProgress<string>? progress = null)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Azure STT] Long transcription: {audioFilePath}");
                progress?.Report("Transcribing with Azure Speech-to-Text...");

                var speechConfig = SpeechConfig.FromSubscription(apiKey, region);
                speechConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, "10000");
                speechConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "2000");

                using (var audioConfig = AudioConfig.FromWavFileInput(audioFilePath))
                using (var recognizer = new SpeechRecognizer(speechConfig, audioConfig))
                {
                    var tcs = new TaskCompletionSource<string>();
                    var fullTranscript = new System.Text.StringBuilder();

                    recognizer.Recognizing += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Result.Text))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Azure STT] Recognizing: {e.Result.Text}");
                        }
                    };

                    recognizer.Recognized += (s, e) =>
                    {
                        if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrEmpty(e.Result.Text))
                        {
                            System.Diagnostics.Debug.WriteLine($"[Azure STT] Recognized: {e.Result.Text}");
                            fullTranscript.AppendLine(e.Result.Text);
                        }
                    };

                    recognizer.Canceled += (s, e) =>
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azure STT] Canceled: {e.Reason}");

                        if (e.Reason == CancellationReason.Error)
                        {
                            System.Diagnostics.Debug.WriteLine($"[Azure STT] Error: {e.ErrorCode} - {e.ErrorDetails}");
                            tcs.TrySetException(new Exception($"Azure STT error: {e.ErrorCode} - {e.ErrorDetails}"));
                        }
                        else
                        {
                            tcs.TrySetResult(fullTranscript.ToString());
                        }
                    };

                    recognizer.SessionStopped += (s, e) =>
                    {
                        System.Diagnostics.Debug.WriteLine($"[Azure STT] Session stopped");
                        tcs.TrySetResult(fullTranscript.ToString());
                    };

                    await recognizer.StartContinuousRecognitionAsync();

                    var transcript = await tcs.Task;

                    await recognizer.StopContinuousRecognitionAsync();

                    System.Diagnostics.Debug.WriteLine($"[Azure STT] Complete: {transcript.Length} chars");
                    return transcript;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Azure STT] Exception: {ex.Message}");
                throw;
            }
        }
    }
}
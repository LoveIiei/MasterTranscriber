using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TranscribeMeetingUI
{
    public partial class MainWindow : Window
    {

        private Recorder recorder;
        private Transcriber transcriber;
        private Summarizer summarizer;
        private ChunkedProcessor chunkedProcessor;
        private RealTimeTranscriber realTimeTranscriber;
        private DispatcherTimer recordingTimer;
        private DateTime recordingStartTime;
        private bool isRecording = false;
        private bool microphoneEnabled = true;
        private bool realTimeEnabled = false;
        private string lastTranscript = "";
        private string lastSummary = "";
        private AppSettings appSettings;
        private bool showRenderedMarkdown = true;
        private DeepLTranslator? translator;
        private string originalTranscript = "";
        private string translatedTranscript = "";
        private bool showingTranslation = false;
        private string currentContext = "meeting";
        private bool isImportedFile = false;
        private string? importedFilePath = null;

        public MainWindow()
        {
            InitializeComponent();
            appSettings = AppSettings.LoadFromFile();

        // Add the settings button handler:
            recorder = new Recorder();
            transcriber = new Transcriber(appSettings);
            if (appSettings.EnableSummary)
            {
                summarizer = new Summarizer(appSettings);
                summarizer.SetContext(currentContext); // Set initial context
            }
            chunkedProcessor = new ChunkedProcessor();
            realTimeTranscriber = new RealTimeTranscriber();
            recordingTimer = new DispatcherTimer();
            recordingTimer.Interval = TimeSpan.FromSeconds(1);
            recordingTimer.Tick += RecordingTimer_Tick;

            // Real-time transcriber events
            realTimeTranscriber.TranscriptUpdated += RealTimeTranscriber_TranscriptUpdated;
            realTimeTranscriber.StatusUpdated += RealTimeTranscriber_StatusUpdated;
            realTimeTranscriber.ChunkReadyForProcessing += RealTimeTranscriber_ChunkReadyForProcessing;
            if (appSettings.EnableTranslation && !string.IsNullOrEmpty(appSettings.DeepLKey))
            {
                translator = new DeepLTranslator(appSettings.DeepLKey, appSettings.TargetLanguage);
                TranslateButton.Visibility = Visibility.Visible;
            }

        }
        private string WHISPER_EXE_PATH => appSettings.WhisperExePath;
        private string WHISPER_MODEL_PATH => appSettings.WhisperModelPath;
        private string OLLAMA_URL => appSettings.OllamaUrl;
        private string OLLAMA_MODEL => appSettings.OllamaModel;
        private string OUTPUT_WAV_PATH => Path.Combine(appSettings.OutputDirectory, "my_recording.wav");

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow(appSettings);
            if (settingsWindow.ShowDialog() == true)
            {
                appSettings = settingsWindow.Settings;

                transcriber = new Transcriber(appSettings);
                if (appSettings.EnableSummary)
                {
                    summarizer = new Summarizer(appSettings);
                    summarizer.SetContext(currentContext); // Restore context after settings change
                }
                // Recreate translator
                if (appSettings.EnableTranslation && !string.IsNullOrEmpty(appSettings.DeepLKey))
                {
                    translator = new DeepLTranslator(appSettings.DeepLKey, appSettings.TargetLanguage);
                }
                else
                {
                    translator = null;
                }

                System.Diagnostics.Debug.WriteLine("[UI] Settings updated and services reinitialized");
            }
        }

        private void RecordingTimer_Tick(object sender, EventArgs e)
        {
            var elapsed = DateTime.Now - recordingStartTime;
            RecordingDuration.Text = elapsed.ToString(@"mm\:ss");
        }

        private void ToggleMarkdown_Click(object sender, RoutedEventArgs e)
        {
            showRenderedMarkdown = !showRenderedMarkdown;

            if (showRenderedMarkdown)
            {
                SummaryMarkdownViewer.Visibility = Visibility.Visible;
                SummaryTextViewer.Visibility = Visibility.Collapsed;
                ToggleMarkdownButton.Content = "📝";
                ToggleMarkdownButton.ToolTip = "Show raw markdown";
            }
            else
            {
                SummaryMarkdownViewer.Visibility = Visibility.Collapsed;
                SummaryTextViewer.Visibility = Visibility.Visible;
                ToggleMarkdownButton.Content = "🎨";
                ToggleMarkdownButton.ToolTip = "Show rendered markdown";
            }
        }

        // Update wherever you set summary text:
        private void UpdateSummary(string summary)
        {
            lastSummary = summary;
            SummaryMarkdown.Markdown = summary;  // For rendered view
            SummaryText.Text = summary;          // For raw view
        }

        private void ContextComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ContextComboBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                currentContext = item.Tag.ToString()!;
                summarizer.SetContext(currentContext);
                System.Diagnostics.Debug.WriteLine($"[UI] Context changed to: {currentContext}");
            }
        }

        private void MicToggle_Click(object sender, RoutedEventArgs e)
        {
            microphoneEnabled = MicToggle.IsChecked == true;
            MicStatusText.Text = microphoneEnabled ? "Enabled" : "Disabled";
            MicStatusText.Foreground = microphoneEnabled ?
                new SolidColorBrush(Color.FromRgb(76, 175, 80)) :
                new SolidColorBrush(Color.FromRgb(158, 158, 158));

            System.Diagnostics.Debug.WriteLine($"[UI] Microphone clicked: {microphoneEnabled}");
        }

        private void RealTimeToggle_Click(object sender, RoutedEventArgs e)
        {
            realTimeEnabled = RealTimeToggle.IsChecked == true;
            RealTimeStatusText.Text = realTimeEnabled ? "Enabled" : "Disabled";
            RealTimeStatusText.Foreground = realTimeEnabled ?
                new SolidColorBrush(Color.FromRgb(76, 175, 80)) :
                new SolidColorBrush(Color.FromRgb(158, 158, 158));

            System.Diagnostics.Debug.WriteLine($"[UI] Real-time clicked: {realTimeEnabled}");
        }

        private async void TranslateButton_Click(object sender, RoutedEventArgs e)
        {
            // Allow clicking during real-time recording OR after recording completes
            if (string.IsNullOrEmpty(lastTranscript) && !isRecording)
                return;

            try
            {
                if (!showingTranslation)
                {
                    // Show translation
                    if (isRecording && realTimeEnabled) // CHECK BOTH - must be actively recording in real-time mode
                    {
                        // For active real-time recording, use live segments
                        showingTranslation = true;
                        TranslateButton.Content = "🔄";
                        TranslateButton.ToolTip = "Show original";

                        var allSegments = realTimeTranscriber.GetAllSegments();

                        if (allSegments.Count > 0)
                        {
                            RealTimeTranscriber_TranscriptUpdated(this, new TranscriptUpdateEventArgs
                            {
                                Segment = allSegments.FirstOrDefault() ?? new TranscriptSegment(),
                                AllSegments = allSegments
                            });
                        }
                        else
                        {
                            TranscriptText.Text = "⏳ Waiting for first segment...\n(Translation will appear automatically)";
                        }
                    }
                    else
                    {
                        // Standard mode OR real-time after recording stopped - translate on demand
                        if (string.IsNullOrEmpty(translatedTranscript))
                        {
                            TranslateButton.IsEnabled = false;
                            TranslationStatusText.Text = "Translating...";
                            TranslationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0));

                            var progress = new Progress<string>(status =>
                            {
                                Dispatcher.Invoke(() => TranslationStatusText.Text = status);
                            });

                            translatedTranscript = await translator!.TranslateBatchAsync(lastTranscript, progress);
                            TranslateButton.IsEnabled = true;
                        }

                        originalTranscript = TranscriptText.Text;
                        TranscriptText.Text = translatedTranscript;
                        showingTranslation = true;
                        TranslateButton.Content = "🔄";
                        TranslateButton.ToolTip = "Show original";
                    }

                    TranslationStatusText.Text = $"Translated to {GetLanguageName(appSettings.TargetLanguage)}";
                    TranslationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                }
                else
                {
                    // Show original
                    showingTranslation = false;
                    TranslateButton.Content = "🌐";
                    TranslateButton.ToolTip = "Translate transcript";
                    TranslationStatusText.Text = "";

                    if (isRecording && realTimeEnabled) // CHECK BOTH
                    {
                        // Trigger UI refresh for active real-time recording
                        var allSegments = realTimeTranscriber.GetAllSegments();

                        if (allSegments.Count > 0)
                        {
                            RealTimeTranscriber_TranscriptUpdated(this, new TranscriptUpdateEventArgs
                            {
                                Segment = allSegments.FirstOrDefault() ?? new TranscriptSegment(),
                                AllSegments = allSegments
                            });
                        }
                        else
                        {
                            TranscriptText.Text = "⏳ Waiting for first segment...";
                        }
                    }
                    else
                    {
                        // Standard mode or stopped recording
                        if (string.IsNullOrEmpty(originalTranscript))
                        {
                            originalTranscript = lastTranscript;
                        }
                        TranscriptText.Text = originalTranscript;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Translation failed: {ex.Message}", "Translation Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                TranslateButton.IsEnabled = true;
                TranslationStatusText.Text = "Translation failed";
                TranslationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            }
        }

        private async void ImportFileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Filter = "Audio/Video Files|*.mp3;*.mp4;*.m4a;*.wav;*.aac;*.wma;*.flac;*.ogg;*.avi;*.mov;*.mkv;*.wmv;*.flv|" +
                             "Audio Files|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.ogg|" +
                             "Video Files|*.mp4;*.avi;*.mov;*.mkv;*.wmv;*.flv|" +
                             "All Files|*.*",
                    Title = "Select Audio or Video File",
                    Multiselect = false
                };

                if (openDialog.ShowDialog() == true)
                {
                    string filePath = openDialog.FileName;

                    if (!AudioConverter.IsSupportedFormat(filePath))
                    {
                        MessageBox.Show("Unsupported file format. Please select an audio or video file.",
                            "Unsupported Format", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    // Disable UI during processing
                    ImportFileButton.IsEnabled = false;
                    RecordButton.IsEnabled = false;
                    MicToggle.IsEnabled = false;
                    RealTimeToggle.IsEnabled = false;
                    ContextComboBox.IsEnabled = false;

                    StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    StatusText.Text = "Processing";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 152, 0));
                    ProgressBar.Visibility = Visibility.Visible;

                    string formatDesc = AudioConverter.GetFormatDescription(filePath);
                    ProgressText.Text = $"Importing {formatDesc}...";

                    TranscriptText.Text = "Processing imported file...";
                    TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                    SummaryText.Text = "Waiting for processing to complete...";
                    SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));

                    var progress = new Progress<string>(status =>
                    {
                        Dispatcher.Invoke(() => ProgressText.Text = status);
                    });

                    // Convert to WAV if needed
                    string wavFilePath;
                    if (Path.GetExtension(filePath).ToLower() == ".wav")
                    {
                        wavFilePath = filePath;
                    }
                    else
                    {
                        wavFilePath = await AudioConverter.ConvertToWavAsync(filePath, progress);
                    }

                    importedFilePath = wavFilePath;
                    isImportedFile = true;

                    // Process the file (transcribe + summarize)
                    await ProcessImportedFileAsync(wavFilePath, progress);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import file: {ex.Message}", "Import Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                TranscriptText.Text = $"❌ Import failed: {ex.Message}";
                TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));

                ResetImportUI();
            }
        }

        private async Task ProcessImportedFileAsync(string wavFilePath, IProgress<string> progress)
        {
            try
            {
                // Transcribe
                var transcriptionResult = await chunkedProcessor.TranscribeAudioAsync(
                    wavFilePath, transcriber, progress);

                if (string.IsNullOrWhiteSpace(transcriptionResult.FullTranscript))
                {
                    TranscriptText.Text = "⚠️ Transcription failed or produced no text.";
                    TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    ResetImportUI();
                    return;
                }

                lastTranscript = transcriptionResult.FullTranscript;

                // Show transcript immediately
                TranscriptText.Text = transcriptionResult.FullTranscript;
                TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));

                StatusText.Text = "Transcription Complete";
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                string chunkInfo = transcriptionResult.ChunkCount > 1 ?
                    $" (processed in {transcriptionResult.ChunkCount} chunks)" : "";

                // Show translate button if enabled
                if (appSettings.EnableTranslation && translator != null)
                {
                    TranslateButton.Visibility = Visibility.Visible;
                    translatedTranscript = "";
                    showingTranslation = false;
                    TranslationStatusText.Text = "";
                }

                // Summarize if enabled
                if (appSettings.EnableSummary)
                {
                    StatusText.Text = "Summarizing";
                    StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(156, 39, 176));

                    lastSummary = await chunkedProcessor.SummarizeTranscriptAsync(
                        transcriptionResult, summarizer, progress);

                    UpdateSummary(lastSummary);
                }
                else
                {
                    SummaryText.Text = "Summary generation is disabled in settings.";
                    SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                }

                // Complete
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                StatusText.Text = "Complete";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                ProgressText.Text = $"✅ Import complete{chunkInfo}!";
                ProgressBar.Visibility = Visibility.Collapsed;
                ExportButton.IsEnabled = true;

                MessageBox.Show($"File processed successfully{chunkInfo}!",
                    "Import Complete", MessageBoxButton.OK, MessageBoxImage.Information);

                // Clean up converted file if it was created
                if (importedFilePath != null && importedFilePath.Contains("_converted.wav"))
                {
                    try
                    {
                        File.Delete(importedFilePath);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                throw;
            }
            finally
            {
                ResetImportUI();
            }
        }

        private void ResetImportUI()
        {
            ImportFileButton.IsEnabled = true;
            RecordButton.IsEnabled = true;
            MicToggle.IsEnabled = true;
            RealTimeToggle.IsEnabled = true;
            ContextComboBox.IsEnabled = true;
            isImportedFile = false;
        }

        private string GetLanguageName(string code)
        {
            var languages = new Dictionary<string, string>
            {
                {"EN-US", "English"}, {"ES", "Spanish"}, {"FR", "French"},
                {"DE", "German"}, {"IT", "Italian"}, {"JA", "Japanese"},
                {"KO", "Korean"}, {"ZH", "Chinese"}
            };
            return languages.TryGetValue(code, out var name) ? name : code;
        }


        private void RealTimeTranscriber_StatusUpdated(object? sender, string status)
        {
            Dispatcher.Invoke(() =>
            {
                ProgressText.Text = status;
            });
        }

        private void RealTimeTranscriber_TranscriptUpdated(object? sender, TranscriptUpdateEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var sb = new System.Text.StringBuilder();

                // Check if we're currently showing translation
                if (showingTranslation && translator != null)
                {
                    // Show translated version
                    foreach (var segment in e.AllSegments.OrderBy(s => s.StartTime))
                    {
                        sb.AppendLine($"[{FormatTimeSpan(segment.StartTime)} - {FormatTimeSpan(segment.EndTime)}]");

                        // Try to get translated version
                        string displayText = realTimeTranscriber.GetTranslatedSegment(segment.ChunkNumber);
                        if (string.IsNullOrEmpty(displayText))
                        {
                            displayText = segment.Transcript + " [Translating...]";
                        }

                        sb.AppendLine(displayText);
                        sb.AppendLine();
                    }

                    // Update translation status
                    TranslationStatusText.Text = $"Translated to {GetLanguageName(appSettings.TargetLanguage)}";
                    TranslationStatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                }
                else
                {
                    // Show original version
                    foreach (var segment in e.AllSegments.OrderBy(s => s.StartTime))
                    {
                        sb.AppendLine($"[{FormatTimeSpan(segment.StartTime)} - {FormatTimeSpan(segment.EndTime)}]");
                        sb.AppendLine(segment.Transcript);
                        sb.AppendLine();
                    }
                }

                TranscriptText.Text = sb.ToString();
                TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
            });
        }

        private async void RealTimeTranscriber_ChunkReadyForProcessing(object? sender, ChunkToProcess chunk)
        {
            try
            {
                string transcript = await transcriber.TranscribeAsync(chunk.FilePath);

                realTimeTranscriber.AddTranscriptSegment(
                    chunk.ChunkNumber,
                    chunk.StartTime,
                    chunk.EndTime,
                    transcript);

                try
                {
                    File.Delete(chunk.FilePath);
                    if (File.Exists(chunk.FilePath + ".txt"))
                        File.Delete(chunk.FilePath + ".txt");
                }
                catch { }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing real-time chunk: {ex.Message}");
            }
        }

        private string FormatTimeSpan(TimeSpan ts)
        {
            return ts.Hours > 0
                ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private async void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isRecording)
            {
                try
                {
                    isRecording = true;
                    recordingStartTime = DateTime.Now;

                    RecordButton.Content = "⬛ Stop Recording";
                    RecordButton.Style = (Style)FindResource("StopButton");
                    StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    StatusText.Text = "Recording";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                    MicToggle.IsEnabled = false;
                    RealTimeToggle.IsEnabled = false;
                    RecordingDuration.Visibility = Visibility.Visible;
                    ExportButton.IsEnabled = false;
                    if (appSettings.EnableTranslation && translator != null)
                    {
                        translatedTranscript = ""; // Reset cache
                        showingTranslation = false;
                        TranslationStatusText.Text = "";
                        TranslateButton.Content = "🌐";
                    }

                    System.Diagnostics.Debug.WriteLine($"[UI] Starting recording - Mic: {microphoneEnabled}, RealTime: {realTimeEnabled}");

                    if (realTimeEnabled)
                    {
                        ProgressText.Text = "Recording in real-time mode...";
                        TranscriptText.Text = $"⏳ Transcription will appear here in real-time...\n(First segment appears after {appSettings.RealTimeChunkSeconds} seconds)";
                        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                        UpdateSummary("Summary will be generated when recording stops...");
                        SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));

                        realTimeTranscriber.StartRecording(OUTPUT_WAV_PATH, microphoneEnabled, translator, appSettings.RealTimeChunkSeconds);
                    }
                    else
                    {
                        ProgressText.Text = "Recording in progress...";
                        TranscriptText.Text = "Recording... Transcript will appear after you stop.";
                        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                        UpdateSummary("Waiting for recording to finish...");
                        SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));

                        recorder.StartRecording(OUTPUT_WAV_PATH, microphoneEnabled);
                    }

                    recordingTimer.Start();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to start recording: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetUI();
                }
            }
            else
            {
                try
                {
                    recordingTimer.Stop();
                    RecordButton.IsEnabled = false;
                    ProgressText.Text = "Stopping recording...";
                    ProgressBar.Visibility = Visibility.Visible;

                    if (realTimeEnabled)
                    {
                        await realTimeTranscriber.StopRecordingAsync();

                        lastTranscript = realTimeTranscriber.GetFullTranscript();

                        if (string.IsNullOrWhiteSpace(lastTranscript))
                        {
                            TranscriptText.Text = "⚠️ No transcript was generated.";
                            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                            ResetUI();
                            realTimeTranscriber.CleanupChunkFiles();
                            return;
                        }

                        if (showingTranslation && translator != null)
                        {
                            translatedTranscript = realTimeTranscriber.GetTranslatedTranscript();
                            originalTranscript = lastTranscript;
                        }

                        if (appSettings.EnableSummary)
                        {
                            StatusText.Text = "Summarizing";
                            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(156, 39, 176));
                            ProgressText.Text = "Generating AI summary...";

                            lastSummary = await summarizer.SummarizeAsync(lastTranscript);
                            UpdateSummary(lastSummary);
                            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                        }
                        else
                        {
                            UpdateSummary("Summary generation is disabled in settings.");
                            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                        }

                        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        StatusText.Text = "Complete";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        ProgressText.Text = "✅ Processing complete!";
                        ProgressBar.Visibility = Visibility.Collapsed;
                        ExportButton.IsEnabled = true;

                        realTimeTranscriber.CleanupChunkFiles();
                        string modeText = "real-time mode";
                        MessageBox.Show($"Recording processed successfully in {modeText}!",
                            "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        // Replace the chunked processing section with:
                        await recorder.StopRecordingAsync();
                        DebugAudioFile(OUTPUT_WAV_PATH);

                        var progress = new Progress<string>(status =>
                        {
                            Dispatcher.Invoke(() => ProgressText.Text = status);
                        });

                        // STEP 1: Transcribe
                        var transcriptionResult = await chunkedProcessor.TranscribeAudioAsync(
    OUTPUT_WAV_PATH, transcriber, progress);

                        if (string.IsNullOrWhiteSpace(transcriptionResult.FullTranscript))
                        {
                            TranscriptText.Text = "⚠️ Transcription failed.";
                            TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                            ResetUI();
                            return;
                        }

                        lastTranscript = transcriptionResult.FullTranscript;

                        // SHOW TRANSCRIPT IMMEDIATELY!
                        TranscriptText.Text = transcriptionResult.FullTranscript;
                        TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                        if (appSettings.EnableSummary) { 
                            StatusText.Text = "Summarizing";
                            StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(156, 39, 176));

                            lastSummary = await chunkedProcessor.SummarizeTranscriptAsync(
                                            transcriptionResult, summarizer, progress);

                            UpdateSummary(lastSummary);
                            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(33, 33, 33));
                        }
                        else
                        {
                            UpdateSummary("Summary generation is disabled in settings.");
                            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(158, 158, 158));
                        }
                        StatusText.Text = "Transcription Complete";
                        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));

                        string chunkInfo = transcriptionResult.ChunkCount > 1 ?
                            $" (processed in {transcriptionResult.ChunkCount} chunks)" : "";
                        ProgressText.Text = $"✅ Transcription complete{chunkInfo}!";

                        // STEP 2: Summarize (can be skipped if user disables it later)


                        // Final status
                        StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                        StatusText.Text = "Complete";
                        ProgressText.Text = $"✅ Processing complete{chunkInfo}!";
                        ExportButton.IsEnabled = true;

                        string modeText = realTimeEnabled ? "real-time mode" : "standard mode";
                    }
                    //MessageBox.Show($"Recording processed successfully in {modeText}!",
                    //    "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"An error occurred: {ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    TranscriptText.Text = $"❌ Error: {ex.Message}";
                    TranscriptText.Foreground = new SolidColorBrush(Color.FromRgb(244, 67, 54));
                }
                finally
                {
                    ResetUI();
                    try
                    {
                        if (File.Exists(OUTPUT_WAV_PATH + ".txt"))
                            File.Delete(OUTPUT_WAV_PATH + ".txt");
                    }
                    catch { }
                }
            }
        }

        private void DebugAudioFile(string audioPath)
        {
            try
            {
                using (var reader = new NAudio.Wave.AudioFileReader(audioPath))
                {
                    System.Diagnostics.Debug.WriteLine($"\n=== AUDIO FILE DIAGNOSTICS ===");
                    System.Diagnostics.Debug.WriteLine($"File Size: {new FileInfo(audioPath).Length / (1024.0 * 1024.0):F2} MB");
                    System.Diagnostics.Debug.WriteLine($"Wave Format: {reader.WaveFormat}");
                    System.Diagnostics.Debug.WriteLine($"Total Time (from NAudio): {reader.TotalTime}");
                    System.Diagnostics.Debug.WriteLine($"Total Time Seconds: {reader.TotalTime.TotalSeconds}");
                    System.Diagnostics.Debug.WriteLine($"===============================\n");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error reading audio file: {ex.Message}");
            }
        }

        private void ResetUI()
        {
            isRecording = false;
            RecordButton.Content = "⚫ Start Recording";
            RecordButton.Style = (Style)FindResource("ModernButton");
            RecordButton.IsEnabled = true;
            MicToggle.IsEnabled = true;
            RealTimeToggle.IsEnabled = true;
            RecordingDuration.Visibility = Visibility.Collapsed;
            RecordingDuration.Text = "00:00";
            ProgressBar.Visibility = Visibility.Collapsed;

            ResetImportUI();

            if (string.IsNullOrEmpty(lastTranscript))
            {
                StatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(189, 189, 189));
                StatusText.Text = "Ready";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(117, 117, 117));
                ProgressText.Text = "Ready to record";
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "Text Files (*.txt)|*.txt|Markdown Files (*.md)|*.md",
                    DefaultExt = ".md",
                    FileName = $"Meeting_Summary_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    string content = $"MEETING SUMMARY\n";
                    content += $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n";
                    content += $"TRANSCRIPT:\n{new string('-', 60)}\n{lastTranscript}\n\n";
                    content += $"AI SUMMARY:\n{new string('-', 60)}\n{lastSummary}\n";

                    File.WriteAllText(saveDialog.FileName, content);
                    MessageBox.Show($"Successfully exported!",
                        "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export: {ex.Message}", "Export Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
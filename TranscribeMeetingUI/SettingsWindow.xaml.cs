using Microsoft.Win32;
using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TranscribeMeetingUI
{
    public partial class SettingsWindow : Window
    {
        public AppSettings Settings { get; private set; }

        public SettingsWindow(AppSettings currentSettings)
        {
            InitializeComponent();
            Settings = currentSettings.Clone();
            LoadSettings();
        }

        private void LoadSettings()
        {
            // Transcription provider
            TranscriptionProviderCombo.SelectedIndex = Settings.UseAzureSTT ? 1 : 0;
            WhisperPathTextBox.Text = Settings.WhisperExePath;
            WhisperModelPathTextBox.Text = Settings.WhisperModelPath;
            AzureSTTKeyTextBox.Text = Settings.AzureSTTKey;
            AzureRegionTextBox.Text = Settings.AzureRegion;

            // Summary provider
            SummaryProviderCombo.SelectedIndex = Settings.UseOpenRouter ? 1 : 0;
            OllamaModelTextBox.Text = Settings.OllamaModel;
            OllamaUrlTextBox.Text = Settings.OllamaUrl;
            OpenRouterKeyTextBox.Text = Settings.OpenRouterKey;
            OpenRouterModelTextBox.Text = Settings.OpenRouterModel;

            // Translation
            EnableTranslationCheckBox.IsChecked = Settings.EnableTranslation;
            DeepLKeyTextBox.Text = Settings.DeepLKey;

            // Set target language
            foreach (ComboBoxItem item in TargetLanguageCombo.Items)
            {
                if (item.Tag?.ToString() == Settings.TargetLanguage)
                {
                    TargetLanguageCombo.SelectedItem = item;
                    break;
                }
            }

            // Real-time
            RealTimeIntervalTextBox.Text = Settings.RealTimeChunkSeconds.ToString();

            // General
            EnableSummaryCheckBox.IsChecked = Settings.EnableSummary;
            AutoExportCheckBox.IsChecked = Settings.AutoExport;
        }

        private void TranscriptionProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LocalWhisperPanel == null || AzureSTTPanel == null) return;

            bool isLocal = TranscriptionProviderCombo.SelectedIndex == 0;
            LocalWhisperPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
            AzureSTTPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SummaryProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LocalOllamaPanel == null || OpenRouterPanel == null) return;

            bool isLocal = SummaryProviderCombo.SelectedIndex == 0;
            LocalOllamaPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
            OpenRouterPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
        }

        private void EnableTranslation_Changed(object sender, RoutedEventArgs e)
        {
            if (TranslationPanel == null) return;
            TranslationPanel.Visibility = EnableTranslationCheckBox.IsChecked == true ?
                Visibility.Visible : Visibility.Collapsed;
        }

        private void BrowseWhisperPath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Executable Files (*.exe)|*.exe|All Files (*.*)|*.*",
                Title = "Select Whisper CLI Executable"
            };

            if (dialog.ShowDialog() == true)
            {
                WhisperPathTextBox.Text = dialog.FileName;
            }
        }

        private void BrowseWhisperModel_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Model Files (*.bin)|*.bin|All Files (*.*)|*.*",
                Title = "Select Whisper Model File"
            };

            if (dialog.ShowDialog() == true)
            {
                WhisperModelPathTextBox.Text = dialog.FileName;
            }
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextNumeric(e.Text);
        }

        private static bool IsTextNumeric(string text)
        {
            return Regex.IsMatch(text, "^[0-9]+$");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate settings
            if (!ValidateSettings())
                return;

            // Save transcription settings
            Settings.UseAzureSTT = TranscriptionProviderCombo.SelectedIndex == 1;
            Settings.WhisperExePath = WhisperPathTextBox.Text;
            Settings.WhisperModelPath = WhisperModelPathTextBox.Text;
            Settings.AzureSTTKey = AzureSTTKeyTextBox.Text;
            Settings.AzureRegion = AzureRegionTextBox.Text;

            // Save summary settings
            Settings.UseOpenRouter = SummaryProviderCombo.SelectedIndex == 1;
            Settings.OllamaModel = OllamaModelTextBox.Text;
            Settings.OllamaUrl = OllamaUrlTextBox.Text;
            Settings.OpenRouterKey = OpenRouterKeyTextBox.Text;
            Settings.OpenRouterModel = OpenRouterModelTextBox.Text;

            // Save translation settings
            Settings.EnableTranslation = EnableTranslationCheckBox.IsChecked == true;
            Settings.DeepLKey = DeepLKeyTextBox.Text;
            Settings.TargetLanguage = (TargetLanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "EN-US";

            // Save real-time settings
            if (int.TryParse(RealTimeIntervalTextBox.Text, out int interval))
            {
                Settings.RealTimeChunkSeconds = Math.Max(5, Math.Min(300, interval)); // Clamp between 5-300
            }

            // Save general settings
            Settings.EnableSummary = EnableSummaryCheckBox.IsChecked == true;
            Settings.AutoExport = AutoExportCheckBox.IsChecked == true;

            // Save to config file
            Settings.SaveToFile();

            DialogResult = true;
            Close();
        }

        private bool ValidateSettings()
        {
            // Validate local Whisper settings
            if (!Settings.UseAzureSTT)
            {
                if (string.IsNullOrWhiteSpace(WhisperPathTextBox.Text))
                {
                    MessageBox.Show("Please specify the Whisper CLI path.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                if (string.IsNullOrWhiteSpace(WhisperModelPathTextBox.Text))
                {
                    MessageBox.Show("Please specify the Whisper model path.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(AzureSTTKeyTextBox.Text))
                {
                    MessageBox.Show("Please enter your Azure Speech API key.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // Validate summary settings
            if (EnableSummaryCheckBox.IsChecked == true)
            {
                if (Settings.UseOpenRouter && string.IsNullOrWhiteSpace(OpenRouterKeyTextBox.Text))
                {
                    MessageBox.Show("Please enter your OpenRouter API key.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // Validate translation settings
            if (EnableTranslationCheckBox.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(DeepLKeyTextBox.Text))
                {
                    MessageBox.Show("Please enter your DeepL API key.", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            // Validate real-time interval
            if (!int.TryParse(RealTimeIntervalTextBox.Text, out int interval) || interval < 5 || interval > 300)
            {
                MessageBox.Show("Real-time interval must be between 5 and 300 seconds.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
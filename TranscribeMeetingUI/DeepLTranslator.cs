using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TranscribeMeetingUI
{
    public class DeepLTranslator
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly string apiKey;
        private readonly string targetLanguage;
        private const string DEEPL_API_URL = "https://api-free.deepl.com/v2/translate";

        public DeepLTranslator(string apiKey, string targetLanguage)
        {
            this.apiKey = apiKey;
            this.targetLanguage = targetLanguage;
        }

        public async Task<string> TranslateAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            try
            {
                System.Diagnostics.Debug.WriteLine($"[DeepL] Translating {text.Length} chars to {targetLanguage}");

                var requestBody = new
                {
                    text = new[] { text },
                    target_lang = targetLanguage
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                using var request = new HttpRequestMessage(HttpMethod.Post, DEEPL_API_URL);
                request.Headers.Add("Authorization", $"DeepL-Auth-Key {apiKey}");
                request.Content = content;

                var response = await httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"[DeepL] Error: {response.StatusCode} - {errorBody}");
                    throw new Exception($"DeepL API error ({response.StatusCode}): {errorBody}");
                }

                string responseBody = await response.Content.ReadAsStringAsync();
                using (JsonDocument doc = JsonDocument.Parse(responseBody))
                {
                    var translations = doc.RootElement.GetProperty("translations");
                    if (translations.GetArrayLength() > 0)
                    {
                        var translatedText = translations[0].GetProperty("text").GetString();
                        System.Diagnostics.Debug.WriteLine($"[DeepL] Success: {translatedText?.Length ?? 0} chars");
                        return translatedText ?? text;
                    }
                }

                return text;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DeepL] Exception: {ex.Message}");
                throw;
            }
        }

        public async Task<string> TranslateBatchAsync(string text, IProgress<string>? progress = null)
        {
            // For very long texts, split by paragraphs and translate in batches
            var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            var translatedParagraphs = new System.Collections.Generic.List<string>();

            int total = paragraphs.Length;
            for (int i = 0; i < total; i++)
            {
                progress?.Report($"Translating... {i + 1}/{total}");

                // Translate paragraph
                string translated = await TranslateAsync(paragraphs[i]);
                translatedParagraphs.Add(translated);

                // Small delay to avoid rate limiting
                if (i < total - 1)
                    await Task.Delay(100);
            }

            return string.Join("\n\n", translatedParagraphs);
        }
    }
}
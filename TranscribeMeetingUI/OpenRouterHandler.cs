using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TranscribeMeetingUI
{
    public class OpenRouterHandler
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly string apiKey;
        private readonly string modelName;
        private const string OPENROUTER_URL = "https://openrouter.ai/api/v1/chat/completions";
        private string currentContext = "meeting";

        public OpenRouterHandler(string apiKey, string modelName)
        {
            this.apiKey = apiKey;
            this.modelName = modelName;

            if (!httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
        }

        public void SetContext(string context)
        {
            currentContext = context.ToLower();
        }

        public async Task<string> SummarizeAsync(string transcript)
        {
            string prompt = BuildPrompt(transcript, currentContext);
            return await GetCompletionAsync(prompt);
        }

        public async Task<string> SummarizeChunkAsync(string chunkTranscript, int chunkNumber)
        {
            string prompt = BuildChunkPrompt(chunkTranscript, chunkNumber, currentContext);
            return await GetCompletionAsync(prompt);
        }

        public async Task<string> SummarizeCombinedChunksAsync(string combinedSummaries, int totalChunks)
        {
            string prompt = BuildCombinedPrompt(combinedSummaries, totalChunks, currentContext);
            return await GetCompletionAsync(prompt);
        }

        private string BuildPrompt(string transcript, string context)
        {
            return context switch
            {
                "meeting" => $@"Here is a transcript of a meeting. Please provide a concise summary with:
- Overview of the meeting
- Key discussion points
- Decisions made
- Action items and owners
- Next steps

Transcript:
""""""
{transcript}
""""""",

                "lecture" => $@"Here is a transcript of a lecture. Please provide a comprehensive summary with:
- Main topic and learning objectives
- Key concepts and theories explained
- Important examples or case studies mentioned
- Key takeaways for students

Transcript:
""""""
{transcript}
""""""",

                "interview" => $@"Here is a transcript of an interview. Please provide a summary with:
- Background of the interviewee
- Main topics discussed
- Key insights and perspectives shared
- Notable quotes or statements
- Conclusion or key takeaways

Transcript:
""""""
{transcript}
""""""",

                "podcast" => $@"Here is a transcript of a podcast episode. Please provide a summary with:
- Episode topic and theme
- Main discussion points
- Interesting insights or stories shared
- Guest perspectives (if applicable)
- Key takeaways for listeners

Transcript:
""""""
{transcript}
""""""",

                _ => $@"Here is a transcript. Please provide a concise summary with key points and highlights.

Transcript:
""""""
{transcript}
"""""""
            };
        }

        private string BuildChunkPrompt(string chunkTranscript, int chunkNumber, string context)
        {
            string contextType = context == "lecture" ? "lecture segment" :
                                 context == "interview" ? "interview segment" :
                                 context == "podcast" ? "podcast segment" : "segment";

            return $@"This is segment {chunkNumber} of a longer {contextType}. Summarize the key points discussed in this segment. Keep it concise (3-5 bullet points).

Transcript:
""""""
{chunkTranscript}
""""""";
        }

        private string BuildCombinedPrompt(string combinedSummaries, int totalChunks, string context)
        {
            return context switch
            {
                "meeting" => $@"I have summaries from {totalChunks} segments of a long meeting. Please create a comprehensive final summary that includes:

1. Overall meeting overview and purpose
2. Main topics discussed (organized chronologically or by theme)
3. Key decisions made
4. Action items and owners
5. Next steps and follow-ups

Here are the segment summaries:

{combinedSummaries}

Provide a well-structured, comprehensive summary of the entire meeting.",

                "lecture" => $@"I have summaries from {totalChunks} segments of a long lecture. Please create a comprehensive final summary that includes:

1. Lecture title and main topic
2. Key concepts and theories covered (in order presented)
3. Important examples or demonstrations
4. Critical insights for students
5. Summary of key takeaways

Here are the segment summaries:

{combinedSummaries}

Provide a well-structured, comprehensive summary of the entire lecture.",

                "interview" => $@"I have summaries from {totalChunks} segments of a long interview. Please create a comprehensive final summary that includes:

1. Interview overview and context
2. Main topics covered
3. Key insights from the interviewee
4. Notable quotes or memorable moments
5. Overall conclusions

Here are the segment summaries:

{combinedSummaries}

Provide a well-structured, comprehensive summary of the entire interview.",

                "podcast" => $@"I have summaries from {totalChunks} segments of a long podcast episode. Please create a comprehensive final summary that includes:

1. Episode overview and main theme
2. Key discussion topics (in order)
3. Interesting stories or insights shared
4. Guest contributions (if applicable)
5. Main takeaways for listeners

Here are the segment summaries:

{combinedSummaries}

Provide a well-structured, comprehensive summary of the entire podcast.",

                _ => $@"I have summaries from {totalChunks} segments. Please create a comprehensive final summary that includes:

1. Overall overview
2. Main topics discussed
3. Key insights
4. Important points
5. Takeaways

Here are the segment summaries:

{combinedSummaries}

Provide a well-structured, comprehensive summary."
            };
        }

        private async Task<string> GetCompletionAsync(string userMessage)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[OpenRouter] Calling API with model: {modelName}");

                var messages = new List<object>
                {
                    new { role = "user", content = userMessage }
                };

                var requestBody = new
                {
                    model = modelName,
                    messages = messages
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Set authorization header for this request
                using var request = new HttpRequestMessage(HttpMethod.Post, OPENROUTER_URL);
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
                request.Content = content;

                var response = await httpClient.SendAsync(request);

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"[OpenRouter] Error: {response.StatusCode} - {errorBody}");
                    throw new Exception($"OpenRouter API error ({response.StatusCode}): {errorBody}");
                }

                string responseBody = await response.Content.ReadAsStringAsync();

                using (JsonDocument doc = JsonDocument.Parse(responseBody))
                {
                    var message = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message")
                        .GetProperty("content")
                        .GetString();

                    System.Diagnostics.Debug.WriteLine($"[OpenRouter] Success: {message?.Length ?? 0} chars");
                    return message ?? "No response content received.";
                }
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenRouter] HTTP Error: {ex.StatusCode} - {ex.Message}");
                throw new Exception($"OpenRouter API call failed: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OpenRouter] Exception: {ex.Message}");
                throw;
            }
        }
    }
}
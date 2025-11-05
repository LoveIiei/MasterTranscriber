using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace TranscribeMeetingUI
{
    public class Summarizer
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private readonly AppSettings settings;
        private OpenRouterHandler? openRouterHandler;
        private string currentContext = "meeting"; // Default context

        public Summarizer(AppSettings settings)
        {
            this.settings = settings;

            if (settings.UseOpenRouter && !string.IsNullOrEmpty(settings.OpenRouterKey))
            {
                openRouterHandler = new OpenRouterHandler(settings.OpenRouterKey, settings.OpenRouterModel);
            }
        }

        public void SetContext(string context)
        {
            currentContext = context.ToLower();
            System.Diagnostics.Debug.WriteLine($"[Summarizer] Context set to: {currentContext}");
        }

        public async Task<string> SummarizeAsync(string transcript)
        {
            if (settings.UseOpenRouter)
            {
                return await SummarizeWithOpenRouterAsync(transcript);
            }
            else
            {
                return await SummarizeWithOllamaAsync(transcript);
            }
        }

        public async Task<string> SummarizeChunkAsync(string chunkTranscript, int chunkNumber)
        {
            if (settings.UseOpenRouter)
            {
                if (openRouterHandler == null)
                {
                    throw new Exception("OpenRouter is not configured.");
                }
                openRouterHandler.SetContext(currentContext);
                return await openRouterHandler.SummarizeChunkAsync(chunkTranscript, chunkNumber);
            }
            else
            {
                return await SummarizeChunkWithOllamaAsync(chunkTranscript, chunkNumber);
            }
        }

        public async Task<string> SummarizeCombinedChunksAsync(string combinedSummaries, int totalChunks)
        {
            if (settings.UseOpenRouter)
            {
                if (openRouterHandler == null)
                {
                    throw new Exception("OpenRouter is not configured.");
                }
                openRouterHandler.SetContext(currentContext);
                return await openRouterHandler.SummarizeCombinedChunksAsync(combinedSummaries, totalChunks);
            }
            else
            {
                return await SummarizeCombinedChunksWithOllamaAsync(combinedSummaries, totalChunks);
            }
        }

        private async Task<string> SummarizeWithOpenRouterAsync(string transcript)
        {
            if (openRouterHandler == null)
            {
                throw new Exception("OpenRouter is not configured. Please check your API key.");
            }
            openRouterHandler.SetContext(currentContext);
            return await openRouterHandler.SummarizeAsync(transcript);
        }

        private async Task<string> SummarizeWithOllamaAsync(string transcript)
        {
            string prompt = BuildPrompt(transcript, currentContext);
            return await CallOllamaAsync(prompt);
        }

        private async Task<string> SummarizeChunkWithOllamaAsync(string chunkTranscript, int chunkNumber)
        {
            string prompt = BuildChunkPrompt(chunkTranscript, chunkNumber, currentContext);
            return await CallOllamaAsync(prompt);
        }

        private async Task<string> SummarizeCombinedChunksWithOllamaAsync(string combinedSummaries, int totalChunks)
        {
            string prompt = BuildCombinedPrompt(combinedSummaries, totalChunks, currentContext);
            return await CallOllamaAsync(prompt);
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

        private async Task<string> CallOllamaAsync(string prompt)
        {
            var requestBody = new OllamaChatRequest
            {
                Model = settings.OllamaModel,
                Messages = new[] { new OllamaMessage { Role = "user", Content = prompt } },
                Stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync($"{settings.OllamaUrl}/api/chat", content);

            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException e)
            {
                throw new Exception($"Failed to call Ollama API. Is Ollama running at {settings.OllamaUrl}? Error: {e.Message}");
            }

            var responseString = await response.Content.ReadAsStringAsync();
            var ollamaResponse = JsonSerializer.Deserialize<OllamaChatResponse>(responseString);

            return ollamaResponse?.Message?.Content ?? "No summary content received.";
        }

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
}
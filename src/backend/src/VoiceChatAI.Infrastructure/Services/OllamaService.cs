using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VoiceChatAI.Infrastructure.Services;

/// <summary>
/// Service for communicating with Ollama (Llama 3) for AI-powered features.
/// </summary>
public class OllamaService
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(HttpClient httpClient, IOptions<OllamaOptions> options, ILogger<OllamaService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generates a meeting summary from recent transcripts.
    /// </summary>
    public async Task<string> GenerateSummaryAsync(string transcriptsText, string? voiceMetricsSummary = null, CancellationToken cancellationToken = default)
    {
        var voiceContext = !string.IsNullOrEmpty(voiceMetricsSummary)
            ? $"\n\nVoice Metrics (for context):\n{voiceMetricsSummary}"
            : string.Empty;

        var prompt = $@"You are an AI meeting assistant. Analyze the following meeting transcript and provide a concise summary of the main topics discussed.

Transcript:
{transcriptsText}{voiceContext}

Provide a brief summary (2-3 sentences) of the key topics discussed.";

        return await CallOllamaAsync(prompt, cancellationToken);
    }

    /// <summary>
    /// Detects if the meeting topic has changed based on recent transcripts.
    /// </summary>
    public async Task<TopicDetectionResult> DetectTopicChangeAsync(string recentTranscripts, string? voiceMetricsSummary = null, CancellationToken cancellationToken = default)
    {
        var voiceContext = !string.IsNullOrEmpty(voiceMetricsSummary)
            ? $"\n\nVoice Metrics (for context):\n{voiceMetricsSummary}"
            : string.Empty;

        var prompt = $@"You are an AI meeting assistant. Analyze the following recent meeting transcripts and determine if the topic has changed.

Recent transcripts:
{recentTranscripts}{voiceContext}

If the topic has changed, respond with:
TOPIC_CHANGED: true
NEW_TOPIC: [brief description of new topic]
CONFIDENCE: [0.0-1.0]

If the topic has NOT changed, respond with:
TOPIC_CHANGED: false";

        var response = await CallOllamaAsync(prompt, cancellationToken);
        return ParseTopicDetection(response);
    }

    /// <summary>
    /// Generates advice for improving the meeting.
    /// </summary>
    public async Task<string> GenerateAdviceAsync(string transcriptsText, string? voiceMetricsSummary = null, CancellationToken cancellationToken = default)
    {
        var voiceContext = !string.IsNullOrEmpty(voiceMetricsSummary)
            ? $"\n\nVoice Metrics (for context):\n{voiceMetricsSummary}"
            : string.Empty;

        var prompt = $@"You are an AI meeting coach. Analyze the following meeting transcript and provide practical advice to improve the meeting.

Transcript:
{transcriptsText}{voiceContext}

Provide 1-2 specific, actionable suggestions for improving the meeting. Be concise and constructive.";

        return await CallOllamaAsync(prompt, cancellationToken);
    }

    /// <summary>
    /// Analyzes speakers in the meeting transcript for fatigue, gender, and break recommendations.
    /// </summary>
    public async Task<SpeakerAnalysisResult> AnalyzeSpeakerAsync(string transcriptsText, string? voiceMetricsSummary = null, CancellationToken cancellationToken = default)
    {
        var voiceContext = !string.IsNullOrEmpty(voiceMetricsSummary)
            ? $"\n\nVoice Metrics (for context):\n{voiceMetricsSummary}"
            : string.Empty;

        var prompt = $@"You are an AI meeting assistant. Analyze the following meeting transcript and determine speaker characteristics.

Transcript:
{transcriptsText}{voiceContext}

Respond in JSON format only (no markdown, no code blocks):
{{
  ""speakerCount"": number,
  ""speakers"": [
    {{""id"": ""speaker_0"", ""gender"": ""male/female"", ""fatigueLevel"": 0.0-1.0}}
  ],
  ""needsBreak"": true/false,
  ""breakReason"": ""string"",
  ""shouldPostpone"": true/false,
  ""postponeReason"": ""string""
}}";

        var response = await CallOllamaAsync(prompt, cancellationToken);
        return ParseSpeakerAnalysis(response);
    }

    /// <summary>
    /// Suggests alternative ideas for the current discussion topic.
    /// </summary>
    public async Task<string> SuggestAlternativeIdeaAsync(string transcriptsText, string? voiceMetricsSummary = null, CancellationToken cancellationToken = default)
    {
        var voiceContext = !string.IsNullOrEmpty(voiceMetricsSummary)
            ? $"\n\nVoice Metrics (for context):\n{voiceMetricsSummary}"
            : string.Empty;

        var prompt = $@"You are an AI brainstorming assistant. Based on the current meeting discussion, suggest an alternative idea or approach that the participants might not have considered.

Current discussion:
{transcriptsText}{voiceContext}

Suggest one alternative idea or perspective. Be creative but relevant.";

        return await CallOllamaAsync(prompt, cancellationToken);
    }

    private async Task<string> CallOllamaAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            var request = new OllamaRequest
            {
                Model = _options.Model,
                Prompt = prompt,
                Stream = false,
                Options = new OllamaOptionsConfig
                {
                    Temperature = 0.7f,
                    NumPredict = 512
                }
            };

            var response = await _httpClient.PostAsJsonAsync($"{_options.BaseUrl}/api/generate", request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(cancellationToken: cancellationToken);
            return result?.Response?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Ollama API");
            return string.Empty;
        }
    }

    private static SpeakerAnalysisResult ParseSpeakerAnalysis(string response)
    {
        var result = new SpeakerAnalysisResult();

        if (string.IsNullOrEmpty(response))
            return result;

        try
        {
            // Try to parse as JSON directly
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<SpeakerAnalysisResult>(response, jsonOptions);
            if (parsed != null)
                return parsed;
        }
        catch (JsonException)
        {
            // Try to extract JSON from the response (in case Ollama wraps it in text)
            try
            {
                var startIdx = response.IndexOf('{');
                var endIdx = response.LastIndexOf('}');
                if (startIdx >= 0 && endIdx > startIdx)
                {
                    var jsonPart = response[startIdx..(endIdx + 1)];
                    var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var parsed = JsonSerializer.Deserialize<SpeakerAnalysisResult>(jsonPart, jsonOptions);
                    if (parsed != null)
                        return parsed;
                }
            }
            catch
            {
                // Ignore parse errors, return default
            }
        }

        return result;
    }

    private static TopicDetectionResult ParseTopicDetection(string response)
    {
        var result = new TopicDetectionResult { TopicChanged = false };

        if (string.IsNullOrEmpty(response))
            return result;

        if (response.Contains("TOPIC_CHANGED: true", StringComparison.OrdinalIgnoreCase))
        {
            result.TopicChanged = true;

            var newTopicLine = response.Split('\n')
                .FirstOrDefault(l => l.StartsWith("NEW_TOPIC:", StringComparison.OrdinalIgnoreCase));
            if (newTopicLine != null)
                result.NewTopic = newTopicLine["NEW_TOPIC:".Length..].Trim();

            var confidenceLine = response.Split('\n')
                .FirstOrDefault(l => l.StartsWith("CONFIDENCE:", StringComparison.OrdinalIgnoreCase));
            if (confidenceLine != null && float.TryParse(confidenceLine["CONFIDENCE:".Length..].Trim(), out var confidence))
                result.Confidence = confidence;
        }

        return result;
    }
}

public class OllamaOptions
{
    public const string SectionName = "Ollama";
    public string BaseUrl { get; set; } = "http://ollama:11434";
    public string Model { get; set; } = "llama3:8b";
}

public class OllamaRequest
{
    public string Model { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public bool Stream { get; set; }
    public OllamaOptionsConfig? Options { get; set; }
}

public class OllamaOptionsConfig
{
    public float Temperature { get; set; } = 0.7f;
    public int NumPredict { get; set; } = 512;
}

public class OllamaResponse
{
    public string? Response { get; set; }
    public bool Done { get; set; }
}

public class TopicDetectionResult
{
    public bool TopicChanged { get; set; }
    public string? NewTopic { get; set; }
    public float Confidence { get; set; }
}

public class SpeakerAnalysisResult
{
    public int SpeakerCount { get; set; }
    public List<SpeakerInfo> Speakers { get; set; } = new();
    public bool NeedsBreak { get; set; }
    public string BreakReason { get; set; } = string.Empty;
    public bool ShouldPostpone { get; set; }
    public string PostponeReason { get; set; } = string.Empty;
}

public class SpeakerInfo
{
    public string Id { get; set; } = string.Empty;
    public string Gender { get; set; } = "unknown";
    public double FatigueLevel { get; set; }
}

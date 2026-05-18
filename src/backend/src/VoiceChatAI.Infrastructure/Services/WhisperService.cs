using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VoiceChatAI.Infrastructure.Services;

/// <summary>
/// Configuration options for Whisper transcription service.
/// </summary>
public class WhisperOptions
{
    public const string SectionName = "Whisper";
    public string BaseUrl { get; set; } = "http://whisper-livekit:8080";
    public int TimeoutSeconds { get; set; } = 300;
    public string Model { get; set; } = "base";
}

/// <summary>
/// Result of a file transcription request.
/// </summary>
public class WhisperTranscriptionResult
{
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public double Duration { get; set; }
    public List<WhisperSegment> Segments { get; set; } = new();
}

public class WhisperSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// HTTP client for calling the WhisperLiveKit transcription service.
/// Used for offline transcription of recorded meeting audio files.
/// </summary>
public class WhisperService
{
    private readonly HttpClient _httpClient;
    private readonly WhisperOptions _options;
    private readonly ILogger<WhisperService> _logger;

    public WhisperService(HttpClient httpClient, IOptions<WhisperOptions> options, ILogger<WhisperService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    /// <summary>
    /// Transcribes an audio file by sending it to the Whisper HTTP API.
    /// </summary>
    /// <param name="audioFilePath">Path to the audio file (OGG, WAV, MP3, etc.)</param>
    /// <param name="language">Optional language code (e.g., "en", "ru"). Auto-detected if null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Transcription result with text, language, and segments.</returns>
    public async Task<WhisperTranscriptionResult> TranscribeFileAsync(
        string audioFilePath,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(audioFilePath))
            {
                _logger.LogError("Audio file not found: {Path}", audioFilePath);
                return new WhisperTranscriptionResult();
            }

            var fileInfo = new FileInfo(audioFilePath);
            _logger.LogInformation("Transcribing file: {Path} ({Size} bytes)", audioFilePath, fileInfo.Length);

            using var formData = new MultipartFormDataContent();
            using var fileStream = File.OpenRead(audioFilePath);
            using var streamContent = new StreamContent(fileStream);

            streamContent.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
            formData.Add(streamContent, "audio", fileInfo.Name);

            if (!string.IsNullOrEmpty(language))
            {
                formData.Add(new StringContent(language), "language");
            }

            var response = await _httpClient.PostAsync("/transcribe", formData, cancellationToken);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<WhisperTranscriptionResult>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            _logger.LogInformation("Transcription complete: {Length} chars, language: {Language}",
                result?.Text?.Length ?? 0, result?.Language ?? "unknown");

            return result ?? new WhisperTranscriptionResult();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error calling Whisper service at {Url}", _options.BaseUrl);
            return new WhisperTranscriptionResult();
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Whisper transcription timed out for file: {Path}", audioFilePath);
            return new WhisperTranscriptionResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transcribing file: {Path}", audioFilePath);
            return new WhisperTranscriptionResult();
        }
    }

    /// <summary>
    /// Checks if the Whisper service is healthy and available.
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

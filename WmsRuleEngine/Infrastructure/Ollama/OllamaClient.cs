using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WmsRuleEngine.Infrastructure.Ollama;

// ─── Configuration ─────────────────────────────────────────────────────────────

public class OllamaOptions
{
    public const string SectionName = "Ollama";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "llama3";
    public int TimeoutSeconds { get; set; } = 120;
    public double Temperature { get; set; } = 0.1;  // Low temp for deterministic JSON
    public int MaxRetries { get; set; } = 3;
}

// ─── Request / Response DTOs ──────────────────────────────────────────────────

public record OllamaGenerateRequest(
    string model,
    string prompt,
    bool stream = false,
    OllamaOptions? options = null
);

public record OllamaGenerateResponse(
    string model,
    string response,
    bool done,
    string? done_reason
);

public record OllamaChatMessage(string role, string content);

public record OllamaChatRequest(
    string model,
    List<OllamaChatMessage> messages,
    bool stream = false
);

public record OllamaChatResponse(
    string model,
    OllamaChatMessage message,
    bool done
);

// ─── Ollama Client ────────────────────────────────────────────────────────────

public interface IOllamaClient
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
    Task<string> ChatAsync(List<OllamaChatMessage> messages, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

public class OllamaClient : IOllamaClient
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public OllamaClient(HttpClient http, IOptions<OllamaOptions> options, ILogger<OllamaClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _http.BaseAddress = new Uri(_options.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
    {
        var request = new
        {
            model = _options.Model,
            prompt,
            stream = false,
            options = new { temperature = _options.Temperature }
        };

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                _logger.LogDebug("Ollama generate attempt {Attempt}/{MaxRetries}", attempt, _options.MaxRetries);

                var response = await _http.PostAsJsonAsync("/api/generate", request, ct);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonOptions, ct)
                    ?? throw new InvalidOperationException("Empty response from Ollama");

                _logger.LogDebug("Ollama response received. Done: {Done}", result.done);
                return result.response;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Ollama attempt {Attempt} failed, retrying...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }

        throw new InvalidOperationException($"Ollama failed after {_options.MaxRetries} attempts");
    }

    public async Task<string> ChatAsync(List<OllamaChatMessage> messages, CancellationToken ct = default)
    {
        var request = new
        {
            model = _options.Model,
            messages,
            stream = false,
            options = new { temperature = _options.Temperature }
        };

        for (int attempt = 1; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                var response = await _http.PostAsJsonAsync("/api/chat", request, ct);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<OllamaChatResponse>(JsonOptions, ct)
                    ?? throw new InvalidOperationException("Empty chat response from Ollama");

                return result.message.content;
            }
            catch (Exception ex) when (attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Ollama chat attempt {Attempt} failed, retrying...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }

        throw new InvalidOperationException($"Ollama chat failed after {_options.MaxRetries} attempts");
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

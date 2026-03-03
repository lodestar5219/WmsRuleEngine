using System.Text.Json;
using Microsoft.Extensions.Logging;
using WmsRuleEngine.Domain.Models;
using WmsRuleEngine.Infrastructure.Ollama;
using WmsRuleEngine.Infrastructure.RAG;
using WmsRuleEngine.Infrastructure.Repositories;

namespace WmsRuleEngine.Application.Services;

public interface IRuleGeneratorService
{
    Task<RuleGenerationResult> GenerateFromNaturalLanguageAsync(
        NaturalLanguageRuleRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Orchestrates the full pipeline:
/// 1. Build RAG-augmented prompt with WMS schema context
/// 2. Call Ollama LLM to generate rule JSON
/// 3. Parse and validate the JSON output
/// 4. Optionally validate via a second LLM call
/// 5. Save to repository
/// </summary>
public class RuleGeneratorService : IRuleGeneratorService
{
    private readonly IOllamaClient _ollama;
    private readonly WmsSchemaStore _schema;
    private readonly RulePromptBuilder _promptBuilder;
    private readonly RuleJsonParser _parser;
    private readonly IRuleRepository _repository;
    private readonly ILogger<RuleGeneratorService> _logger;

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public RuleGeneratorService(
        IOllamaClient ollama,
        WmsSchemaStore schema,
        RulePromptBuilder promptBuilder,
        RuleJsonParser parser,
        IRuleRepository repository,
        ILogger<RuleGeneratorService> logger)
    {
        _ollama = ollama;
        _schema = schema;
        _promptBuilder = promptBuilder;
        _parser = parser;
        _repository = repository;
        _logger = logger;
    }

    public async Task<RuleGenerationResult> GenerateFromNaturalLanguageAsync(
        NaturalLanguageRuleRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Generating rule from: {Input}", request.NaturalLanguageInput);

        // ── Step 1: Build system + user prompt with RAG context ───────────────
        var systemPrompt = _promptBuilder.BuildSystemPrompt();
        var userPrompt = _promptBuilder.BuildUserPrompt(request.NaturalLanguageInput, request.ContextKey);

        var messages = new List<OllamaChatMessage>
        {
            new("system", systemPrompt),
            new("user", userPrompt)
        };

        // ── Step 2: Call LLM ──────────────────────────────────────────────────
        string rawResponse;
        try
        {
            rawResponse = await _ollama.ChatAsync(messages, ct);
            _logger.LogDebug("Raw LLM response: {Response}", rawResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM call failed");
            return new RuleGenerationResult
            {
                Success = false,
                ErrorMessage = $"LLM unavailable: {ex.Message}"
            };
        }

        // ── Step 3: Parse + validate JSON output ──────────────────────────────
        var (rule, errors, warnings) = _parser.Parse(rawResponse);

        if (rule is null || errors.Count > 0)
        {
            _logger.LogWarning("Rule generation failed. Errors: {Errors}", string.Join("; ", errors));

            // Retry with error correction prompt
            _logger.LogInformation("Attempting error-correction retry...");
            var retryResult = await RetryWithCorrectionAsync(request, rawResponse, errors, messages, ct);
            if (retryResult is not null) return retryResult;

            return new RuleGenerationResult
            {
                Success = false,
                //RawAiResponse = rawResponse,
                ErrorMessage = string.Join("; ", errors),
                //Warnings = warnings
            };
        }

        // ── Step 4: Apply metadata ────────────────────────────────────────────
        rule.CreatedBy = request.CreatedBy ?? "AI";

        // ── Step 5: Save to repository ────────────────────────────────────────
        var savedRule = await _repository.SaveAsync(rule, ct);
        _logger.LogInformation("Rule saved with ID {RuleId}", savedRule.Id);

        return new RuleGenerationResult
        {
            Success = true,
            Rule = savedRule,
            //RawAiResponse = rawResponse,
            //Warnings = warnings,
            //DetectedContextKey = savedRule.ContextKey
        };
    }

    private async Task<RuleGenerationResult?> RetryWithCorrectionAsync(
        NaturalLanguageRuleRequest request,
        string failedJson,
        List<string> errors,
        List<OllamaChatMessage> originalMessages,
        CancellationToken ct)
    {
        try
        {
            var correctionMessages = new List<OllamaChatMessage>(originalMessages)
            {
                new("assistant", failedJson),
                new("user", $"""
                    The previous response had these issues:
                    {string.Join("\n", errors.Select(e => $"- {e}"))}

                    Please fix these issues and return ONLY valid corrected JSON.
                    Remember to use exact property paths from the schema.
                    """)
            };

            var retryResponse = await _ollama.ChatAsync(correctionMessages, ct);
            var (retryRule, retryErrors, retryWarnings) = _parser.Parse(retryResponse);

            if (retryRule is not null && retryErrors.Count == 0)
            {
                retryRule.CreatedBy = request.CreatedBy ?? "AI";
                var saved = await _repository.SaveAsync(retryRule, ct);
                _logger.LogInformation("Rule saved via correction retry. ID: {RuleId}", saved.Id);

                return new RuleGenerationResult
                {
                    Success = true,
                    Rule = saved,
                    //RawAiResponse = retryResponse,
                   // Warnings = retryWarnings
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error correction retry failed");
        }

        return null;
    }
}

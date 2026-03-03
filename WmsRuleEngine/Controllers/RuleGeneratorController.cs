using Microsoft.AspNetCore.Mvc;
using WmsRuleEngine.Application.Services;
using WmsRuleEngine.Domain.Models;

namespace WmsRuleEngine.API.Controllers;

/// <summary>
/// Main API controller for natural language to rule generation
/// </summary>
[ApiController]
[Route("api/rules")]
[Produces("application/json")]
public class RuleGeneratorController : ControllerBase
{
    private readonly IRuleGeneratorService _generatorService;
    private readonly IRuleManagementService _managementService;
    private readonly ILogger<RuleGeneratorController> _logger;

    public RuleGeneratorController(
        IRuleGeneratorService generatorService,
        IRuleManagementService managementService,
        ILogger<RuleGeneratorController> logger)
    {
        _generatorService = generatorService;
        _managementService = managementService;
        _logger = logger;
    }

    /// <summary>
    /// Convert natural language to a WMS rule using AI + RAG
    /// </summary>
    /// <example>
    /// POST /api/rules/generate
    /// { "naturalLanguageInput": "if operator type is trainee or vehicle battery is below 20 when mission priority is high then Block mission" }
    /// </example>
    [HttpPost("generate")]
    [ProducesResponseType(typeof(RuleGenerationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RuleGenerationResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GenerateRule(
        [FromBody] NaturalLanguageRuleRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.NaturalLanguageInput))
            return BadRequest(new { error = "naturalLanguageInput is required" });

        _logger.LogInformation("Rule generation request: {Input}", request.NaturalLanguageInput);

        var result = await _generatorService.GenerateFromNaturalLanguageAsync(request, ct);

        if (!result.Success)
        {
            if (result.ErrorMessage?.Contains("LLM unavailable") == true)
                return StatusCode(503, result);

            return UnprocessableEntity(result);
        }

        return Ok(result);
    }

    /// <summary>Get all rules</summary>
    [HttpGet]
    public async Task<IActionResult> GetAllRules(CancellationToken ct = default)
    {
        var rules = await _managementService.GetAllRulesAsync(ct);
        return Ok(rules);
    }

    /// <summary>Get rules by context key</summary>
    [HttpGet("context/{contextKey}")]
    public async Task<IActionResult> GetRulesByContext(string contextKey, CancellationToken ct = default)
    {
        var rules = await _managementService.GetRulesByContextAsync(contextKey, ct);
        return Ok(rules);
    }

    /// <summary>Get a specific rule</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetRule(Guid id, CancellationToken ct = default)
    {
        var rule = await _managementService.GetRuleByIdAsync(id, ct);
        return rule is null ? NotFound() : Ok(rule);
    }

    /// <summary>Toggle rule active state</summary>
    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> ToggleRule(Guid id, CancellationToken ct = default)
    {
        try
        {
            var rule = await _managementService.ToggleRuleAsync(id, ct);
            return Ok(rule);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>Delete a rule</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct = default)
    {
        var deleted = await _managementService.DeleteRuleAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>Get WMS schema - available entities and properties for rule building</summary>
    [HttpGet("schema/entities")]
    public IActionResult GetSchemaEntities()
        => Ok(_managementService.GetSchemaEntities());

    /// <summary>Get all available context keys</summary>
    [HttpGet("schema/context-keys")]
    public IActionResult GetContextKeys()
        => Ok(_managementService.GetContextKeys());
}

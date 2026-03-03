using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WmsRuleEngine.Domain.Models;
using WmsRuleEngine.Infrastructure.RAG;

namespace WmsRuleEngine.Infrastructure.Ollama;

/// <summary>
/// Parses the raw LLM output into a WmsRule, with schema validation.
/// Handles common LLM output quirks (extra text, markdown fences, etc.)
/// </summary>
public class RuleJsonParser
{
    private readonly WmsSchemaStore _schema;
    private readonly ILogger<RuleJsonParser> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public RuleJsonParser(WmsSchemaStore schema, ILogger<RuleJsonParser> logger)
    {
        _schema = schema;
        _logger = logger;
    }

    public (WmsRule? rule, List<string> errors, List<string> warnings) Parse(string rawResponse)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Step 1: Extract JSON from potentially noisy LLM output
        var json = ExtractJson(rawResponse);
        if (json is null)
        {
            errors.Add("Could not extract valid JSON from AI response.");
            return (null, errors, warnings);
        }

        // Step 2: Parse JSON into WmsRule
        WmsRule? rule;
        try
        {
            rule = DeserializeRule(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize rule JSON");
            errors.Add($"JSON deserialization failed: {ex.Message}");
            return (null, errors, warnings);
        }

        if (rule is null)
        {
            errors.Add("Parsed rule was null.");
            return (null, errors, warnings);
        }

        // Step 3: Schema validation
        ValidateRule(rule, errors, warnings);

        return errors.Count == 0 ? (rule, errors, warnings) : (null, errors, warnings);
    }

    private string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        raw = raw.Trim();

        // Remove markdown code fences if present
        raw = Regex.Replace(raw, @"^```(json)?\s*", "", RegexOptions.Multiline).Trim();
        raw = Regex.Replace(raw, @"```\s*$", "", RegexOptions.Multiline).Trim();

        // Try to find JSON object boundaries
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');

        if (start < 0 || end < 0 || end <= start) return null;

        var candidate = raw[start..(end + 1)];

        // Validate it's parseable JSON
        try
        {
            JsonNode.Parse(candidate);
            return candidate;
        }
        catch
        {
            _logger.LogWarning("Extracted JSON candidate failed to parse: {Candidate}", candidate[..Math.Min(200, candidate.Length)]);
            return null;
        }
    }

    private WmsRule DeserializeRule(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("Null JSON node");

        var rule = new WmsRule
        {
            Name = node["name"]?.GetValue<string>() ?? "Unnamed Rule",
            Description = node["description"]?.GetValue<string>() ?? "",
            ContextKey = node["contextKey"]?.GetValue<string>() ?? "",
            Priority = node["priority"]?.GetValue<int>() ?? 100,
            StopProcessing = node["stopProcessing"]?.GetValue<bool>() ?? false,
            IsActive = node["isActive"]?.GetValue<bool>() ?? true,
        };

        var rootConditionNode = node["rootCondition"];
        if (rootConditionNode != null)
            rule.RootCondition = ParseConditionNode(rootConditionNode);

        var actionsNode = node["actions"]?.AsArray();
        if (actionsNode != null)
        {
            foreach (var actionNode in actionsNode)
            {
                if (actionNode is null) continue;
                rule.Actions.Add(new RuleAction
                {
                    ActionType = actionNode["actionType"]?.GetValue<string>() ?? "",
                    Message = actionNode["message"]?.GetValue<string>()
                });
            }
        }

        return rule;
    }

    private ConditionNode ParseConditionNode(JsonNode node)
    {
        var type = node["type"]?.GetValue<string>() ?? "";

        if (type == "group")
        {
            var group = new ConditionNode
            {
                Type = "group",
                Operator = node["operator"]?.GetValue<string>() ?? "And",
                Children = new List<ConditionNode>()
            };

            var children = node["children"]?.AsArray();
            if (children != null)
            {
                foreach (var child in children)
                {
                    if (child is not null)
                        group.Children.Add(ParseConditionNode(child));
                }
            }
            return group;
        }
        else // comparison
        {
            var rightOperandNode = node["rightOperand"];
            object? rightOperand = null;

            if (rightOperandNode != null)
            {
                // Preserve numeric types properly
                if (rightOperandNode is JsonValue val)
                {
                    if (val.TryGetValue<int>(out var intVal)) rightOperand = intVal;
                    else if (val.TryGetValue<double>(out var dblVal)) rightOperand = dblVal;
                    else if (val.TryGetValue<bool>(out var boolVal)) rightOperand = boolVal;
                    else rightOperand = val.GetValue<string>();
                }
            }

            return new ConditionNode
            {
                Type = "comparison",
                LeftOperand = node["leftOperand"]?.GetValue<string>(),
                ComparisonOperator = node["comparisonOperator"]?.GetValue<string>()
                                  ?? node["operator"]?.GetValue<string>(), // fallback key name
                RightOperand = rightOperand
            };
        }
    }

    private void ValidateRule(WmsRule rule, List<string> errors, List<string> warnings)
    {
        var validContextKeys = _schema.GetAllContextKeys().Select(c => c.Key).ToHashSet();
        var allProperties = _schema.GetAllEntities()
            .SelectMany(e => e.Properties)
            .ToDictionary(p => p.FullPath, p => p);

        // Validate context key
        if (string.IsNullOrEmpty(rule.ContextKey))
            errors.Add("Rule is missing a contextKey.");
        else if (!validContextKeys.Contains(rule.ContextKey))
            warnings.Add($"ContextKey '{rule.ContextKey}' is not in the known schema. It may be custom.");

        // Validate actions
        var validActionTypes = new HashSet<string> { "Block", "Warn", "Log", "Notify" };
        foreach (var action in rule.Actions)
        {
            if (!validActionTypes.Contains(action.ActionType))
                errors.Add($"Invalid action type: '{action.ActionType}'");
        }

        // Validate conditions recursively
        ValidateConditionNode(rule.RootCondition, allProperties, errors, warnings);
    }

    private void ValidateConditionNode(
        ConditionNode node,
        Dictionary<string, PropertyDefinition> allProperties,
        List<string> errors,
        List<string> warnings)
    {
        if (node.Type == "group")
        {
            var validGroupOps = new HashSet<string> { "And", "Or" };
            if (node.Operator != null && !validGroupOps.Contains(node.Operator))
                errors.Add($"Invalid group operator: '{node.Operator}'. Must be 'And' or 'Or'.");

            foreach (var child in node.Children ?? new List<ConditionNode>())
                ValidateConditionNode(child, allProperties, errors, warnings);
        }
        else if (node.Type == "comparison")
        {
            var validCompOps = new HashSet<string>
            {
                "Equals", "NotEquals", "LessThan", "LessThanOrEquals",
                "GreaterThan", "GreaterThanOrEquals", "Contains", "StartsWith", "In", "NotIn"
            };

            if (string.IsNullOrEmpty(node.LeftOperand))
            {
                errors.Add("Comparison node missing leftOperand.");
                return;
            }

            if (string.IsNullOrEmpty(node.ComparisonOperator))
                errors.Add($"Comparison for '{node.LeftOperand}' missing operator.");
            else if (!validCompOps.Contains(node.ComparisonOperator))
                errors.Add($"Invalid comparison operator '{node.ComparisonOperator}' for '{node.LeftOperand}'.");

            // Validate property path exists in schema
            if (!allProperties.TryGetValue(node.LeftOperand, out var propDef))
            {
                warnings.Add($"Property '{node.LeftOperand}' not found in schema. Ensure it's a valid entity path.");
                return;
            }

            // Validate enum values
            if (propDef.DataType == "enum" && propDef.AllowedValues != null && node.RightOperand != null)
            {
                var rightStr = node.RightOperand.ToString();
                if (!propDef.AllowedValues.Contains(rightStr))
                    errors.Add($"Value '{rightStr}' is not valid for enum '{node.LeftOperand}'. Allowed: [{string.Join(", ", propDef.AllowedValues)}]");
            }

            // Validate number type
            if (propDef.DataType == "number" && node.RightOperand is string)
                warnings.Add($"Property '{node.LeftOperand}' is numeric but rightOperand is a string. Should be a number.");
        }
    }
}

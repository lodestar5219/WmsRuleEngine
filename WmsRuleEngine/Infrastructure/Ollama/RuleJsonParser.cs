using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WmsRuleEngine.Domain.Models;
using WmsRuleEngine.Infrastructure.RAG;

namespace WmsRuleEngine.Infrastructure.Ollama;

/// <summary>
/// Parses raw LLM output into a WmsRule with schema validation.
/// Constructs GroupConditionNode / ComparisonConditionNode so each
/// node type serialises only its own fields — no null bleed.
/// </summary>
public class RuleJsonParser
{
    private readonly WmsSchemaStore _schema;
    private readonly ILogger<RuleJsonParser> _logger;

    public RuleJsonParser(WmsSchemaStore schema, ILogger<RuleJsonParser> logger)
    {
        _schema = schema;
        _logger = logger;
    }

    public (WmsRule? rule, List<string> errors, List<string> warnings) Parse(string rawResponse)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        var json = ExtractJson(rawResponse);
        if (json is null)
        {
            errors.Add("Could not extract valid JSON from AI response.");
            return (null, errors, warnings);
        }

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

        ValidateRule(rule, errors, warnings);
        return errors.Count == 0 ? (rule, errors, warnings) : (null, errors, warnings);
    }

    private string? ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        raw = raw.Trim();
        raw = Regex.Replace(raw, @"^```(json)?\s*", "", RegexOptions.Multiline).Trim();
        raw = Regex.Replace(raw, @"```\s*$", "", RegexOptions.Multiline).Trim();

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start) return null;

        var candidate = raw[start..(end + 1)];
        try
        {
            JsonNode.Parse(candidate);
            return candidate;
        }
        catch
        {
            _logger.LogWarning("Extracted JSON failed to parse: {Snippet}",
                candidate[..Math.Min(200, candidate.Length)]);
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

        var rootNode = node["rootCondition"];
        if (rootNode != null)
            rule.RootCondition = ParseConditionNode(rootNode);

        var actionsArray = node["actions"]?.AsArray();
        if (actionsArray != null)
        {
            foreach (var actionNode in actionsArray)
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
            var group = new GroupConditionNode
            {
                Operator = node["operator"]?.GetValue<string>() ?? "And",
                Children = new List<ConditionNode>()
            };

            var children = node["children"]?.AsArray();
            if (children != null)
                foreach (var child in children)
                    if (child is not null)
                        group.Children.Add(ParseConditionNode(child));

            return group;
        }
        else // "comparison"
        {
            object? rightOperand = null;
            if (node["rightOperand"] is JsonValue val)
            {
                if (val.TryGetValue<int>(out var intVal)) rightOperand = intVal;
                else if (val.TryGetValue<double>(out var dblVal)) rightOperand = dblVal;
                else if (val.TryGetValue<bool>(out var boolVal)) rightOperand = boolVal;
                else rightOperand = val.GetValue<string>();
            }

            return new ComparisonConditionNode
            {
                LeftOperand = node["leftOperand"]?.GetValue<string>() ?? string.Empty,
                // Accept both key names the LLM may output
                ComparisonOperator = node["comparisonOperator"]?.GetValue<string>()
                                  ?? node["operator"]?.GetValue<string>()
                                  ?? string.Empty,
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

        if (string.IsNullOrEmpty(rule.ContextKey))
            errors.Add("Rule is missing a contextKey.");
        else if (!validContextKeys.Contains(rule.ContextKey))
            warnings.Add($"ContextKey '{rule.ContextKey}' not in schema — may be custom.");

        var validActionTypes = new HashSet<string> { "Block", "Warn", "Log", "Notify" };
        foreach (var action in rule.Actions)
            if (!validActionTypes.Contains(action.ActionType))
                errors.Add($"Invalid action type: '{action.ActionType}'");

        ValidateConditionNode(rule.RootCondition, allProperties, errors, warnings);
    }

    private void ValidateConditionNode(
        ConditionNode node,
        Dictionary<string, PropertyDefinition> allProperties,
        List<string> errors,
        List<string> warnings)
    {
        if (node is GroupConditionNode group)
        {
            if (group.Operator != "And" && group.Operator != "Or")
                errors.Add($"Invalid group operator: '{group.Operator}'. Must be 'And' or 'Or'.");

            foreach (var child in group.Children)
                ValidateConditionNode(child, allProperties, errors, warnings);
        }
        else if (node is ComparisonConditionNode cmp)
        {
            var validCompOps = new HashSet<string>
            {
                "Equals", "NotEquals", "LessThan", "LessThanOrEquals",
                "GreaterThan", "GreaterThanOrEquals", "Contains", "StartsWith", "In", "NotIn"
            };

            if (string.IsNullOrEmpty(cmp.LeftOperand))
            {
                errors.Add("Comparison node missing leftOperand.");
                return;
            }

            if (string.IsNullOrEmpty(cmp.ComparisonOperator))
                errors.Add($"Comparison for '{cmp.LeftOperand}' missing operator.");
            else if (!validCompOps.Contains(cmp.ComparisonOperator))
                errors.Add($"Invalid operator '{cmp.ComparisonOperator}' for '{cmp.LeftOperand}'.");

            if (!allProperties.TryGetValue(cmp.LeftOperand, out var propDef))
            {
                warnings.Add($"Property '{cmp.LeftOperand}' not found in schema.");
                return;
            }

            if (propDef.DataType == "enum" && propDef.AllowedValues != null && cmp.RightOperand != null)
            {
                var rightStr = cmp.RightOperand.ToString();
                if (!propDef.AllowedValues.Contains(rightStr))
                    errors.Add($"Value '{rightStr}' not valid for '{cmp.LeftOperand}'. " +
                               $"Allowed: [{string.Join(", ", propDef.AllowedValues)}]");
            }

            if (propDef.DataType == "number" && cmp.RightOperand is string)
                warnings.Add($"'{cmp.LeftOperand}' is numeric but rightOperand is a string.");
        }
    }
}
using WmsRuleEngine.Infrastructure.RAG;

namespace WmsRuleEngine.Infrastructure.Ollama;

/// <summary>
/// Builds structured prompts injected with RAG schema context.
/// This is the core of the AI accuracy - giving the LLM exact schema knowledge.
/// </summary>
public class RulePromptBuilder
{
    private readonly WmsSchemaStore _schema;

    public RulePromptBuilder(WmsSchemaStore schema)
    {
        _schema = schema;
    }

    public string BuildSystemPrompt()
    {
        return """
            You are an expert rule engine configuration assistant for a Warehouse Management System (WMS).
            Your job is to convert natural language rule descriptions into precise, structured JSON rule objects.

            STRICT REQUIREMENTS:
            1. Always respond with ONLY valid JSON - no explanation, no markdown, no backticks.
            2. Use ONLY property paths that exist in the provided schema (leftOperand must match FullPath exactly).
            3. Use ONLY valid comparison operators: Equals, NotEquals, LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals, Contains, StartsWith, In, NotIn
            4. Use ONLY valid action types: Block, Warn, Log, Notify
            5. Use ONLY valid context keys from the schema.
            6. For enum properties, use ONLY the allowed values listed in the schema.
            7. For number comparisons, rightOperand must be a JSON number (not a string).
            8. The rootCondition must always be a "group" node at the top level.
            9. Generate a descriptive name and description based on the rule intent.
            10. Set priority between 1-1000 (higher = evaluated first). Default 100.
            11. Set stopProcessing=true only for Block actions.

            JSON STRUCTURE:
            {
              "name": "string",
              "description": "string",
              "contextKey": "string (from schema)",
              "priority": number,
              "stopProcessing": boolean,
              "isActive": true,
              "rootCondition": { ... },
              "actions": [ { "actionType": "string", "message": "string" } ]
            }

            CONDITION GROUP structure:
            { "type": "group", "operator": "And|Or", "children": [...] }

            CONDITION COMPARISON structure:
            { "type": "comparison", "leftOperand": "Entity.Property", "comparisonOperator": "Operator", "rightOperand": value }
            """;
    }

    public string BuildUserPrompt(string naturalLanguageInput, string? contextKeyHint = null)
    {
        var ragContext = _schema.BuildRagContext();

        var contextHint = contextKeyHint != null
            ? $"\nUSER SPECIFIED CONTEXT KEY: {contextKeyHint} (use this if valid, otherwise infer from schema)\n"
            : "\nInfer the most appropriate contextKey from the schema based on the rule description.\n";

        return $"""
            {ragContext}
            {contextHint}
            NATURAL LANGUAGE RULE TO CONVERT:
            \"{naturalLanguageInput}\"

            Convert the above rule description into a valid WMS rule JSON object.
            Use ONLY schema-defined entity paths and values. Respond with JSON only.
            """;
    }

    public string BuildValidationPrompt(string ruleJson, string originalInput)
    {
        var ragContext = _schema.BuildRagContext();

        // Build the prompt using concatenation to avoid issues with braces inside interpolated raw strings
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ragContext);
        sb.AppendLine();
        sb.Append("ORIGINAL USER INPUT: \"");
        sb.Append(originalInput);
        sb.AppendLine("\"");
        sb.AppendLine();
        sb.AppendLine("GENERATED RULE JSON:");
        sb.AppendLine(ruleJson);
        sb.AppendLine();
        sb.AppendLine("Validate the above rule JSON against the schema. Check:");
        sb.AppendLine("1. All leftOperand values match exact FullPath values in the schema");
        sb.AppendLine("2. All rightOperand values for enum properties match allowed values");
        sb.AppendLine("3. All operators are valid");
        sb.AppendLine("4. The contextKey is valid");
        sb.AppendLine("5. Actions are valid");
        sb.AppendLine();
        sb.AppendLine("If valid, respond with: {\"valid\": true, \"issues\": []}");
        sb.AppendLine("If invalid, respond with: {\"valid\": false, \"issues\": [\"issue1\", \"issue2\", ...]}");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only.");

        return sb.ToString();
    }
}

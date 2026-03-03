using WmsRuleEngine.Infrastructure.RAG;

namespace WmsRuleEngine.Infrastructure.Ollama;

/// <summary>
/// Builds RAG-augmented prompts for the LLM rule generator.
/// Two few-shot examples teach both flat AND/OR groups and nested mixed groups.
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
            Your ONLY job is to convert natural language rule descriptions into precise, structured JSON.

            CRITICAL GROUPING RULES:

            RULE 1 - NEVER create an unnecessary wrapper group.
              If all conditions connect with the SAME operator (all AND or all OR),
              put them as DIRECT children of ONE group. Do NOT wrap each condition in its own group.

              WRONG for "A and B":
                And group
                  child: And group
                           child: comparison A
                  child: comparison B

              CORRECT for "A and B":
                And group
                  child: comparison A
                  child: comparison B

            RULE 2 - Use a nested group ONLY when operators are MIXED (AND inside OR, or OR inside AND).
              "A or (B and C)" requires nesting:
                Or group
                  child: comparison A
                  child: And group
                           child: comparison B
                           child: comparison C

            KEYWORD MAPPING:
              "or" / "either"                           => Or group
              "and" / "when" / "while" / "if...and..."  => And group

            OUTPUT RULES:
            1. Respond with ONLY valid JSON - no explanation, no markdown, no backticks.
            2. leftOperand MUST exactly match a FullPath from the schema (e.g. "Operator.Type").
            3. Valid operator values: Equals, NotEquals, LessThan, LessThanOrEquals,
               GreaterThan, GreaterThanOrEquals, Contains, StartsWith, In, NotIn
            4. Valid actionType values: Block, Warn, Log, Notify
            5. For enum properties use ONLY the allowed values listed in the schema.
            6. rightOperand for number properties MUST be a JSON number (e.g. 20 not "20").
            7. rootCondition MUST always be a "group" node, never a bare comparison.
            8. stopProcessing = true ONLY when actionType is Block.
            9. Write an accurate action message describing the actual condition.
            10. Default priority = 100; safety-critical rules use 200-500.

            NODE SCHEMAS:
            Group node:
              { "type": "group", "operator": "And|Or", "children": [ ...nodes... ] }
            Comparison node:
              { "type": "comparison", "leftOperand": "Entity.Property", "operator": "Operator", "rightOperand": <value> }
            Action:
              { "actionType": "Block|Warn|Log|Notify", "message": "reason" }
            """;
    }

    public string BuildUserPrompt(string naturalLanguageInput, string? contextKeyHint = null)
    {
        var ragContext = _schema.BuildRagContext();

        var contextHint = contextKeyHint != null
            ? $"\nUSER SPECIFIED CONTEXT KEY: {contextKeyHint} (use this if valid, otherwise infer)\n"
            : "\nInfer the most appropriate contextKey from the schema based on the rule description.\n";

        // Built with string.Concat so JSON braces are never misread as C# interpolation
        var fewShot = string.Concat(
            "══════════════════════════════════════════════\n",
            "FEW-SHOT EXAMPLES:\n\n",

            // Example 1 — flat AND (same operator throughout, no nesting needed)
            "EXAMPLE 1 — Two conditions, same operator (AND) => flat group, NO nested groups:\n",
            "Input: \"if vehicle battery is below 20 and mission priority is high then Block\"\n",
            "Logical parse: (Vehicle.BatteryLevel < 20) AND (Mission.Priority = HIGH)\n",
            "Output:\n",
            "{\n",
            "  \"name\": \"Block High Priority Mission with Low Battery\",\n",
            "  \"description\": \"Blocks high priority missions when vehicle battery is low\",\n",
            "  \"contextKey\": \"MISSION_EXECUTION\",\n",
            "  \"priority\": 200,\n",
            "  \"stopProcessing\": true,\n",
            "  \"isActive\": true,\n",
            "  \"rootCondition\": {\n",
            "    \"type\": \"group\",\n",
            "    \"operator\": \"And\",\n",
            "    \"children\": [\n",
            "      { \"type\": \"comparison\", \"leftOperand\": \"Vehicle.BatteryLevel\", \"operator\": \"LessThan\", \"rightOperand\": 20 },\n",
            "      { \"type\": \"comparison\", \"leftOperand\": \"Mission.Priority\", \"operator\": \"Equals\", \"rightOperand\": \"HIGH\" }\n",
            "    ]\n",
            "  },\n",
            "  \"actions\": [{ \"actionType\": \"Block\", \"message\": \"Mission blocked due to low battery and high priority.\" }]\n",
            "}\n",
            "END EXAMPLE 1\n\n",

            // Example 2 — mixed OR/AND (different operators, nesting required)
            "EXAMPLE 2 — Mixed operators (OR at top, AND nested inside) => nested group:\n",
            "Input: \"if operator type is trainee or vehicle battery is below 20 when mission priority is high then Block\"\n",
            "Logical parse: (Operator.Type = Trainee) OR ((Vehicle.BatteryLevel < 20) AND (Mission.Priority = HIGH))\n",
            "Output:\n",
            "{\n",
            "  \"name\": \"Block Risky Mission Execution\",\n",
            "  \"description\": \"Blocks when operator is trainee, or battery is low AND mission is high priority\",\n",
            "  \"contextKey\": \"MISSION_EXECUTION\",\n",
            "  \"priority\": 200,\n",
            "  \"stopProcessing\": true,\n",
            "  \"isActive\": true,\n",
            "  \"rootCondition\": {\n",
            "    \"type\": \"group\",\n",
            "    \"operator\": \"Or\",\n",
            "    \"children\": [\n",
            "      { \"type\": \"comparison\", \"leftOperand\": \"Operator.Type\", \"operator\": \"Equals\", \"rightOperand\": \"Trainee\" },\n",
            "      {\n",
            "        \"type\": \"group\",\n",
            "        \"operator\": \"And\",\n",
            "        \"children\": [\n",
            "          { \"type\": \"comparison\", \"leftOperand\": \"Vehicle.BatteryLevel\", \"operator\": \"LessThan\", \"rightOperand\": 20 },\n",
            "          { \"type\": \"comparison\", \"leftOperand\": \"Mission.Priority\", \"operator\": \"Equals\", \"rightOperand\": \"HIGH\" }\n",
            "        ]\n",
            "      }\n",
            "    ]\n",
            "  },\n",
            "  \"actions\": [{ \"actionType\": \"Block\", \"message\": \"Mission blocked due to unsafe execution conditions.\" }]\n",
            "}\n",
            "END EXAMPLE 2\n",
            "══════════════════════════════════════════════\n"
        );

        return string.Concat(
            ragContext,
            contextHint,
            "\n",
            fewShot,
            "\nNATURAL LANGUAGE RULE TO CONVERT:\n\"",
            naturalLanguageInput,
            "\"\n\n",
            "STEP 1 - List each condition and the connectors between them (and / or / when).\n",
            "STEP 2 - Are ALL connectors the same? If yes: one flat group with all comparisons as direct children.\n",
            "STEP 3 - Are connectors MIXED? If yes: create a nested group only where the operator changes.\n",
            "STEP 4 - Output the complete JSON rule. Respond with JSON only."
        );
    }

    public string BuildValidationPrompt(string ruleJson, string originalInput)
    {
        var ragContext = _schema.BuildRagContext();

        return string.Concat(
            ragContext,
            "\n\nORIGINAL USER INPUT: \"", originalInput, "\"\n\n",
            "GENERATED RULE JSON:\n", ruleJson, "\n\n",
            """
            Validate the rule JSON against the schema. Check:
            1. All leftOperand values exactly match a FullPath in the schema
            2. All rightOperand values for enum properties match the allowed values
            3. All operator values are valid
            4. The contextKey is valid
            5. All actionType values are valid
            6. No unnecessary wrapper groups — a group with a single comparison child
               (other than rootCondition) is always wrong
            7. Logical nesting reflects the original AND/OR/WHEN groupings

            If valid, respond with: {"valid": true, "issues": []}
            If invalid, respond with: {"valid": false, "issues": ["issue1", "issue2", ...]}

            Respond with JSON only.
            """
        );
    }
}
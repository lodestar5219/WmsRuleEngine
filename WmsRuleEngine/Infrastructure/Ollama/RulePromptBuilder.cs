using WmsRuleEngine.Infrastructure.RAG;

namespace WmsRuleEngine.Infrastructure.Ollama;

/// <summary>
/// Builds RAG-augmented prompts for the LLM rule generator.
/// Uses a few-shot example + explicit nesting rules to prevent the LLM
/// from flattening nested And/Or conditions into a single flat group.
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
        // Plain """ raw string - no $ prefix - JSON brace examples are safe literals
        return """
            You are an expert rule engine configuration assistant for a Warehouse Management System (WMS).
            Your ONLY job is to convert natural language rule descriptions into precise, structured JSON.

            ??????????????????????????????????????????????
            CRITICAL: LOGICAL NESTING RULES
            ??????????????????????????????????????????????
            You MUST preserve exact logical structure. NEVER flatten nested conditions.

            KEYWORD MAPPING:
              "or" / "either"                           => OR group
              "and" / "when" / "while" / "if...and..."  => AND group
              Top-level clauses joined by "or"           => top-level Or group
              Conditions joined by "and"/"when" WITHIN a clause => nested And group as a CHILD

            "A or (B and C)" MUST produce:
              Or group
                child 0: comparison A
                child 1: And group
                           child 0: comparison B
                           child 1: comparison C

            NEVER produce a flat Or with 3 children for "A or (B and C)". The And nesting is MANDATORY.

            ??????????????????????????????????????????????
            OUTPUT RULES
            ??????????????????????????????????????????????
            1. Respond with ONLY valid JSON - no explanation, no markdown, no backticks.
            2. leftOperand MUST exactly match a FullPath from the schema (e.g. "Operator.Type").
            3. Valid comparisonOperator: Equals, NotEquals, LessThan, LessThanOrEquals,
               GreaterThan, GreaterThanOrEquals, Contains, StartsWith, In, NotIn
            4. Valid actionType: Block, Warn, Log, Notify
            5. For enum properties use ONLY the allowed values listed in the schema.
            6. rightOperand for number properties MUST be a JSON number (e.g. 20 not "20").
            7. rootCondition MUST always be a "group" node, never a bare comparison.
            8. stopProcessing = true ONLY when actionType is Block.
            9. Write an accurate action message describing the actual unsafe condition.
            10. Default priority = 100; safety-critical rules use 200-500.

            NODE SCHEMAS:
            Group:
              { "type": "group", "operator": "And|Or", "children": [ ...nodes... ] }

            Comparison:
              { "type": "comparison", "leftOperand": "Entity.Property", "comparisonOperator": "Operator", "rightOperand": <value> }

            Action:
              { "actionType": "Block|Warn|Log|Notify", "message": "Human readable reason" }
            """;
    }

    public string BuildUserPrompt(string naturalLanguageInput, string? contextKeyHint = null)
    {
        var ragContext = _schema.BuildRagContext();

        var contextHint = contextKeyHint != null
            ? $"\nUSER SPECIFIED CONTEXT KEY: {contextKeyHint} (use this if valid, otherwise infer)\n"
            : "\nInfer the most appropriate contextKey from the schema based on the rule description.\n";

        // Few-shot example via string.Concat so JSON braces are never
        // misread as C# interpolation placeholders
        var fewShot = string.Concat(
            "??????????????????????????????????????????????\n",
            "FEW-SHOT EXAMPLE - study the nested And inside the Or:\n",
            "Input: \"if operator type is trainee or vehicle battery is below 20 when mission priority is high then Block mission\"\n",
            "Logical parse: (Operator.Type=Trainee)  OR  (Vehicle.BatteryLevel<20  AND  Mission.Priority=HIGH)\n",
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
            "      {\n",
            "        \"type\": \"comparison\",\n",
            "        \"leftOperand\": \"Operator.Type\",\n",
            "        \"comparisonOperator\": \"Equals\",\n",
            "        \"rightOperand\": \"Trainee\"\n",
            "      },\n",
            "      {\n",
            "        \"type\": \"group\",\n",
            "        \"operator\": \"And\",\n",
            "        \"children\": [\n",
            "          {\n",
            "            \"type\": \"comparison\",\n",
            "            \"leftOperand\": \"Vehicle.BatteryLevel\",\n",
            "            \"comparisonOperator\": \"LessThan\",\n",
            "            \"rightOperand\": 20\n",
            "          },\n",
            "          {\n",
            "            \"type\": \"comparison\",\n",
            "            \"leftOperand\": \"Mission.Priority\",\n",
            "            \"comparisonOperator\": \"Equals\",\n",
            "            \"rightOperand\": \"HIGH\"\n",
            "          }\n",
            "        ]\n",
            "      }\n",
            "    ]\n",
            "  },\n",
            "  \"actions\": [\n",
            "    {\n",
            "      \"actionType\": \"Block\",\n",
            "      \"message\": \"Mission blocked due to unsafe execution conditions.\"\n",
            "    }\n",
            "  ]\n",
            "}\n",
            "END OF EXAMPLE\n",
            "??????????????????????????????????????????????\n"
        );

        return string.Concat(
            ragContext,
            contextHint,
            "\n",
            fewShot,
            "\nNATURAL LANGUAGE RULE TO CONVERT:\n\"",
            naturalLanguageInput,
            "\"\n\n",
            "STEP 1 - Identify each condition and the logical connectors (and/or/when) between them.\n",
            "STEP 2 - Build nested groups that exactly mirror the logical structure from Step 1.\n",
            "STEP 3 - Output the complete JSON rule. Respond with JSON only."
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
            3. All comparisonOperator values are valid
            4. The contextKey is valid
            5. All actionType values are valid
            6. Logical nesting correctly reflects the original AND/OR/WHEN groupings

            If valid, respond with: {"valid": true, "issues": []}
            If invalid, respond with: {"valid": false, "issues": ["issue1", "issue2", ...]}

            Respond with JSON only.
            """
        );
    }
}
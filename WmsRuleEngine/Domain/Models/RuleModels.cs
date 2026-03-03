namespace WmsRuleEngine.Domain.Models;

// ─── Core Rule Model ──────────────────────────────────────────────────────────

public class WmsRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ContextKey { get; set; } = string.Empty;
    public int Priority { get; set; } = 100;
    public bool StopProcessing { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public ConditionNode RootCondition { get; set; } = new();
    public List<RuleAction> Actions { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedBy { get; set; } = "AI";
}

public class ConditionNode
{
    public string Type { get; set; } = string.Empty;       // "group" | "comparison"
    public string? Operator { get; set; }                  // Group: "And" | "Or"
    public List<ConditionNode>? Children { get; set; }     // Group children
    public string? LeftOperand { get; set; }               // e.g. "Operator.Type"
    public string? ComparisonOperator { get; set; }        // "Equals" | "LessThan" | etc.
    public object? RightOperand { get; set; }              // e.g. "Trainee", 20
}

public class RuleAction
{
    public string ActionType { get; set; } = string.Empty; // Block | Warn | Log | Notify
    public string? Message { get; set; }
    public Dictionary<string, object>? Parameters { get; set; }
}

// ─── Schema / RAG Context Models ─────────────────────────────────────────────

public class EntityDefinition
{
    public string EntityName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<PropertyDefinition> Properties { get; set; } = new();
}

public class PropertyDefinition
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;   // e.g. "Operator.Type"
    public string DataType { get; set; } = string.Empty;   // string | number | bool | enum
    public List<string>? AllowedValues { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class ContextKeyDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> AvailableEntities { get; set; } = new();
}

// ─── AI Processing Models ─────────────────────────────────────────────────────

public class NaturalLanguageRuleRequest
{
    public string NaturalLanguageInput { get; set; } = string.Empty;
    public string? ContextKey { get; set; }
    public string? CreatedBy { get; set; }
}

public class RuleGenerationResult
{
    public bool Success { get; set; }
    public WmsRule? Rule { get; set; }
    public string? RawAiResponse { get; set; }
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string? DetectedContextKey { get; set; }
}

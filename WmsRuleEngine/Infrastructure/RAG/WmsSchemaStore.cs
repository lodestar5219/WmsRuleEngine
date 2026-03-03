using WmsRuleEngine.Domain.Models;

namespace WmsRuleEngine.Infrastructure.RAG;

/// <summary>
/// In-memory schema store that acts as the RAG knowledge base.
/// In production, replace with a vector DB (Qdrant/Chroma) + embedding search.
/// This store provides the AI with accurate entity/property context.
/// </summary>
public class WmsSchemaStore
{
    private readonly List<EntityDefinition> _entities = new();
    private readonly List<ContextKeyDefinition> _contextKeys = new();

    public WmsSchemaStore()
    {
        SeedSchema();
    }

    public IReadOnlyList<EntityDefinition> GetAllEntities() => _entities.AsReadOnly();
    public IReadOnlyList<ContextKeyDefinition> GetAllContextKeys() => _contextKeys.AsReadOnly();

    public EntityDefinition? GetEntity(string name) =>
        _entities.FirstOrDefault(e => e.EntityName.Equals(name, StringComparison.OrdinalIgnoreCase));

    public List<PropertyDefinition> SearchProperties(string keyword)
    {
        keyword = keyword.ToLower();
        return _entities
            .SelectMany(e => e.Properties)
            .Where(p => p.FullPath.ToLower().Contains(keyword) ||
                        p.Description.ToLower().Contains(keyword))
            .ToList();
    }

    /// <summary>
    /// Builds a compact RAG context string to inject into the LLM prompt.
    /// </summary>
    public string BuildRagContext()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("=== WAREHOUSE MANAGEMENT SYSTEM SCHEMA ===");
        sb.AppendLine();

        sb.AppendLine("--- CONTEXT KEYS (use these for contextKey field) ---");
        foreach (var ctx in _contextKeys)
        {
            sb.AppendLine($"  {ctx.Key}: {ctx.Description}");
            sb.AppendLine($"    Available entities: {string.Join(", ", ctx.AvailableEntities)}");
        }

        sb.AppendLine();
        sb.AppendLine("--- ENTITY PROPERTIES (use exact FullPath for leftOperand) ---");

        foreach (var entity in _entities)
        {
            sb.AppendLine($"[{entity.EntityName}] - {entity.Description}");
            foreach (var prop in entity.Properties)
            {
                var enumValues = prop.AllowedValues != null
                    ? $" | Allowed: [{string.Join(", ", prop.AllowedValues)}]"
                    : "";
                sb.AppendLine($"  - {prop.FullPath} ({prop.DataType}){enumValues} → {prop.Description}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("--- VALID COMPARISON OPERATORS ---");
        sb.AppendLine("  Equals, NotEquals, LessThan, LessThanOrEquals, GreaterThan, GreaterThanOrEquals, Contains, StartsWith, In, NotIn");

        sb.AppendLine();
        sb.AppendLine("--- VALID ACTION TYPES ---");
        sb.AppendLine("  Block (stops execution), Warn (alert only), Log (audit trail), Notify (send notification)");

        return sb.ToString();
    }

    private void SeedSchema()
    {
        // ── Operator ──────────────────────────────────────────────────────────
        _entities.Add(new EntityDefinition
        {
            EntityName = "Operator",
            Category = "operator",
            Description = "Warehouse worker who executes missions",
            Properties = new List<PropertyDefinition>
            {
                new() { Name = "Type", FullPath = "Operator.Type", DataType = "enum",
                        AllowedValues = new() { "Trainee", "Experienced", "Senior", "Supervisor" },
                        Description = "Skill level / role of the operator" },
                new() { Name = "Id", FullPath = "Operator.Id", DataType = "string",
                        Description = "Unique identifier of the operator" },
                new() { Name = "IsActive", FullPath = "Operator.IsActive", DataType = "bool",
                        Description = "Whether operator is currently active/on shift" },
                new() { Name = "CertificationLevel", FullPath = "Operator.CertificationLevel", DataType = "enum",
                        AllowedValues = new() { "None", "Basic", "Advanced", "Expert" },
                        Description = "Safety certification level" },
                new() { Name = "ShiftHoursWorked", FullPath = "Operator.ShiftHoursWorked", DataType = "number",
                        Description = "Hours worked in current shift" },
            }
        });

        // ── OperatorType ──────────────────────────────────────────────────────
        _entities.Add(new EntityDefinition
        {
            EntityName = "OperatorType",
            Category = "operator_type",
            Description = "Definition/classification for operator types",
            Properties = new List<PropertyDefinition>
            {
                new() { Name = "Name", FullPath = "OperatorType.Name", DataType = "string",
                        Description = "Name of the operator type" },
                new() { Name = "MaxMissionsPerShift", FullPath = "OperatorType.MaxMissionsPerShift", DataType = "number",
                        Description = "Maximum missions allowed per shift for this type" },
                new() { Name = "RequiresSupervisor", FullPath = "OperatorType.RequiresSupervisor", DataType = "bool",
                        Description = "Whether this operator type needs supervisor approval" },
            }
        });

        // ── Vehicle ───────────────────────────────────────────────────────────
        _entities.Add(new EntityDefinition
        {
            EntityName = "Vehicle",
            Category = "vehicle",
            Description = "Forklift, AGV, or other warehouse vehicle",
            Properties = new List<PropertyDefinition>
            {
                new() { Name = "BatteryLevel", FullPath = "Vehicle.BatteryLevel", DataType = "number",
                        Description = "Current battery percentage (0-100)" },
                new() { Name = "Type", FullPath = "Vehicle.Type", DataType = "enum",
                        AllowedValues = new() { "Forklift", "AGV", "Reach Truck", "Pallet Jack", "Conveyor" },
                        Description = "Category of vehicle" },
                new() { Name = "Status", FullPath = "Vehicle.Status", DataType = "enum",
                        AllowedValues = new() { "Available", "InUse", "Maintenance", "Charging", "OutOfService" },
                        Description = "Current operational status" },
                new() { Name = "MaxLoadCapacityKg", FullPath = "Vehicle.MaxLoadCapacityKg", DataType = "number",
                        Description = "Maximum load the vehicle can carry in kilograms" },
                new() { Name = "LastMaintenanceDate", FullPath = "Vehicle.LastMaintenanceDate", DataType = "string",
                        Description = "Date of last maintenance check (ISO format)" },
                new() { Name = "IsAutonomous", FullPath = "Vehicle.IsAutonomous", DataType = "bool",
                        Description = "Whether the vehicle is autonomous (AGV)" },
            }
        });

        // ── VehicleType ───────────────────────────────────────────────────────
        _entities.Add(new EntityDefinition
        {
            EntityName = "VehicleType",
            Category = "vehicle_type",
            Description = "Classification definition for vehicle types",
            Properties = new List<PropertyDefinition>
            {
                new() { Name = "Name", FullPath = "VehicleType.Name", DataType = "string",
                        Description = "Name of the vehicle type" },
                new() { Name = "MinBatteryToOperate", FullPath = "VehicleType.MinBatteryToOperate", DataType = "number",
                        Description = "Minimum battery % required to start a mission" },
                new() { Name = "RequiresLicense", FullPath = "VehicleType.RequiresLicense", DataType = "bool",
                        Description = "Whether a special license is required to operate" },
            }
        });

        // ── Mission ───────────────────────────────────────────────────────────
        _entities.Add(new EntityDefinition
        {
            EntityName = "Mission",
            Category = "mission",
            Description = "A warehouse task such as pick, put-away, transfer",
            Properties = new List<PropertyDefinition>
            {
                new() { Name = "Priority", FullPath = "Mission.Priority", DataType = "enum",
                        AllowedValues = new() { "LOW", "MEDIUM", "HIGH", "CRITICAL" },
                        Description = "Urgency level of the mission" },
                new() { Name = "Type", FullPath = "Mission.Type", DataType = "enum",
                        AllowedValues = new() { "Pick", "PutAway", "Transfer", "Inventory", "Replenishment" },
                        Description = "Type of warehouse mission" },
                new() { Name = "Status", FullPath = "Mission.Status", DataType = "enum",
                        AllowedValues = new() { "Pending", "InProgress", "Completed", "Blocked", "Cancelled" },
                        Description = "Current status of the mission" },
                new() { Name = "WeightKg", FullPath = "Mission.WeightKg", DataType = "number",
                        Description = "Total weight of goods in the mission" },
                new() { Name = "IsHazardous", FullPath = "Mission.IsHazardous", DataType = "bool",
                        Description = "Whether the mission involves hazardous materials" },
            }
        });

        // ── HandlingUnitType ──────────────────────────────────────────────────
        _entities.Add(new EntityDefinition
        {
            EntityName = "HandlingUnitType",
            Category = "handling_unit_type",
            Description = "Type of handling unit (pallet, box, container, etc.)",
            Properties = new List<PropertyDefinition>
            {
                new() { Name = "Name", FullPath = "HandlingUnitType.Name", DataType = "string",
                        Description = "Name of the handling unit type" },
                new() { Name = "MaxWeightKg", FullPath = "HandlingUnitType.MaxWeightKg", DataType = "number",
                        Description = "Maximum allowed weight for this unit type" },
                new() { Name = "RequiresSpecialEquipment", FullPath = "HandlingUnitType.RequiresSpecialEquipment", DataType = "bool",
                        Description = "Whether special equipment is needed for handling" },
                new() { Name = "Category", FullPath = "HandlingUnitType.Category", DataType = "enum",
                        AllowedValues = new() { "Pallet", "Box", "Container", "Drum", "Bulk" },
                        Description = "General category of the handling unit" },
            }
        });

        // ── Context Keys ──────────────────────────────────────────────────────
        _contextKeys.AddRange(new[]
        {
            new ContextKeyDefinition
            {
                Key = "MISSION_EXECUTION",
                Description = "Triggered when an operator attempts to start or execute a mission",
                AvailableEntities = new() { "Mission", "Operator", "Vehicle", "HandlingUnitType" }
            },
            new ContextKeyDefinition
            {
                Key = "MISSION_ASSIGNMENT",
                Description = "Triggered when assigning a mission to an operator and vehicle",
                AvailableEntities = new() { "Mission", "Operator", "OperatorType", "Vehicle", "VehicleType" }
            },
            new ContextKeyDefinition
            {
                Key = "VEHICLE_OPERATION",
                Description = "Triggered when a vehicle is started or put into operation",
                AvailableEntities = new() { "Vehicle", "VehicleType", "Operator" }
            },
            new ContextKeyDefinition
            {
                Key = "INVENTORY_CHECK",
                Description = "Triggered during inventory audit or cycle count",
                AvailableEntities = new() { "Mission", "HandlingUnitType", "Operator" }
            },
        });
    }
}

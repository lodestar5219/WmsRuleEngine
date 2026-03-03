using WmsRuleEngine.Domain.Models;
using WmsRuleEngine.Infrastructure.RAG;
using WmsRuleEngine.Infrastructure.Repositories;

namespace WmsRuleEngine.Application.Services;

public interface IRuleManagementService
{
    Task<IReadOnlyList<WmsRule>> GetAllRulesAsync(CancellationToken ct = default);
    Task<WmsRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<WmsRule>> GetRulesByContextAsync(string contextKey, CancellationToken ct = default);
    Task<bool> DeleteRuleAsync(Guid id, CancellationToken ct = default);
    Task<WmsRule> ToggleRuleAsync(Guid id, CancellationToken ct = default);
    IReadOnlyList<EntityDefinition> GetSchemaEntities();
    IReadOnlyList<ContextKeyDefinition> GetContextKeys();
}

public class RuleManagementService : IRuleManagementService
{
    private readonly IRuleRepository _repository;
    private readonly WmsSchemaStore _schema;

    public RuleManagementService(IRuleRepository repository, WmsSchemaStore schema)
    {
        _repository = repository;
        _schema = schema;
    }

    public Task<IReadOnlyList<WmsRule>> GetAllRulesAsync(CancellationToken ct = default)
        => _repository.GetAllAsync(ct);

    public Task<WmsRule?> GetRuleByIdAsync(Guid id, CancellationToken ct = default)
        => _repository.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<WmsRule>> GetRulesByContextAsync(string contextKey, CancellationToken ct = default)
        => _repository.GetByContextKeyAsync(contextKey, ct);

    public Task<bool> DeleteRuleAsync(Guid id, CancellationToken ct = default)
        => _repository.DeleteAsync(id, ct);

    public async Task<WmsRule> ToggleRuleAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await _repository.GetByIdAsync(id, ct)
            ?? throw new KeyNotFoundException($"Rule {id} not found");
        rule.IsActive = !rule.IsActive;
        return await _repository.UpdateAsync(rule, ct);
    }

    public IReadOnlyList<EntityDefinition> GetSchemaEntities() => _schema.GetAllEntities();
    public IReadOnlyList<ContextKeyDefinition> GetContextKeys() => _schema.GetAllContextKeys();
}

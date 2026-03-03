using System.Collections.Concurrent;
using WmsRuleEngine.Domain.Models;

namespace WmsRuleEngine.Infrastructure.Repositories;

public interface IRuleRepository
{
    Task<WmsRule> SaveAsync(WmsRule rule, CancellationToken ct = default);
    Task<WmsRule?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<WmsRule>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WmsRule>> GetByContextKeyAsync(string contextKey, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<WmsRule> UpdateAsync(WmsRule rule, CancellationToken ct = default);
}

/// <summary>
/// In-memory rule repository. 
/// Production: Replace with EF Core + SQL Server/PostgreSQL.
/// </summary>
public class InMemoryRuleRepository : IRuleRepository
{
    private readonly ConcurrentDictionary<Guid, WmsRule> _store = new();

    public Task<WmsRule> SaveAsync(WmsRule rule, CancellationToken ct = default)
    {
        rule.Id = rule.Id == Guid.Empty ? Guid.NewGuid() : rule.Id;
        rule.CreatedAt = DateTime.UtcNow;
        _store[rule.Id] = rule;
        return Task.FromResult(rule);
    }

    public Task<WmsRule?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(id, out var rule) ? rule : null);

    public Task<IReadOnlyList<WmsRule>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WmsRule>>(_store.Values.OrderBy(r => r.Priority).ToList());

    public Task<IReadOnlyList<WmsRule>> GetByContextKeyAsync(string contextKey, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WmsRule>>(
            _store.Values
                  .Where(r => r.ContextKey == contextKey && r.IsActive)
                  .OrderBy(r => r.Priority)
                  .ToList());

    public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_store.TryRemove(id, out _));

    public Task<WmsRule> UpdateAsync(WmsRule rule, CancellationToken ct = default)
    {
        _store[rule.Id] = rule;
        return Task.FromResult(rule);
    }
}

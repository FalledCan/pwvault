namespace PwVault.Core.Tools;

public sealed record HealthReport(IReadOnlySet<Guid> Weak, IReadOnlySet<Guid> Reused)
{
    public static readonly HealthReport Empty = new(new HashSet<Guid>(), new HashSet<Guid>());
}

/// <summary>弱いパスワード・使い回しの検出（FR-14）。端末内で完結し、外部には何も送らない。</summary>
public static class PasswordHealth
{
    public static HealthReport Analyze(IEnumerable<VaultEntry> entries)
    {
        var active = entries.Where(e => !e.Data.IsTrashed && e.Data.Password.Length > 0).ToList();

        var weak = active.Where(e => PasswordStrength.Evaluate(e.Data.Password).IsWeak).Select(e => e.Id).ToHashSet();
        var reused = active
            .GroupBy(e => e.Data.Password, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(e => e.Id))
            .ToHashSet();

        return new HealthReport(weak, reused);
    }
}

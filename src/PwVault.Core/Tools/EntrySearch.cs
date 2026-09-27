namespace PwVault.Core.Tools;

public enum EntrySort
{
    Title,
    UpdatedDesc,
    CreatedDesc,
}

/// <summary>検索・絞り込み・並び替え（FR-06, FR-16）。</summary>
public static class EntrySearch
{
    /// <summary>
    /// タイトル・ユーザーID・URL・タグの部分一致（大文字小文字を区別しない）。
    /// 空白区切りの複数語は AND 条件。お気に入りは常に先頭に並べる。
    /// </summary>
    public static IReadOnlyList<VaultEntry> Filter(IEnumerable<VaultEntry> entries, string? query, string? tag = null,
        EntrySort sort = EntrySort.Title)
    {
        var terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var filtered = entries.Where(e =>
            !e.Data.IsTrashed
            && (tag is null || e.Data.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            && terms.All(t => Matches(e.Data, t)));

        var ordered = filtered.OrderByDescending(e => e.Data.Favorite);
        ordered = sort switch
        {
            EntrySort.UpdatedDesc => ordered.ThenByDescending(e => e.UpdatedAt),
            EntrySort.CreatedDesc => ordered.ThenByDescending(e => e.Data.CreatedAt),
            _ => ordered.ThenBy(e => e.Data.Title, StringComparer.CurrentCultureIgnoreCase),
        };
        return ordered.ToList();
    }

    public static IReadOnlyList<string> AllTags(IEnumerable<VaultEntry> entries) =>
        entries.Where(e => !e.Data.IsTrashed)
            .SelectMany(e => e.Data.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static bool Matches(EntryData d, string term) =>
        d.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || d.Username.Contains(term, StringComparison.OrdinalIgnoreCase)
        || d.Url.Contains(term, StringComparison.OrdinalIgnoreCase)
        || d.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));
}

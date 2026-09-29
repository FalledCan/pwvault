namespace PwVault.Core.Tools;

/// <summary>自動タイプで打つ 1 つの操作（文字列を打つ、または Tab キーを押す）。</summary>
public abstract record AutoTypeAction
{
    public sealed record Text(string Value) : AutoTypeAction;
    public sealed record Tab : AutoTypeAction;
}

/// <summary>
/// 自動タイプ（ゲームやアプリのログイン画面への入力）で、エントリとアプリの紐付けを扱う。
/// アプリは実行ファイル名（例: ffxivboot.exe）で覚える。大文字・小文字と、フォルダ・「.exe」の有無は区別しない。
/// </summary>
public static class AutoTypeMatcher
{
    /// <summary>比べるための形（小文字・ファイル名だけ・「.exe」なし）。</summary>
    public static string Normalize(string app)
    {
        // フォルダの区切りは OS によらず \ と / の両方（Windows のパスを Mac で扱っても同じ結果にする）
        var trimmed = app.Trim().Trim('"');
        var name = trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..].ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    /// <summary>カンマ（、）区切りの入力をアプリの一覧にする（空・重複を除く）。</summary>
    public static List<string> Parse(string text) =>
        text.Split([',', '、', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => Normalize(a).Length > 0)
            .DistinctBy(Normalize)
            .ToList();

    public static bool IsLinked(EntryData data, string app)
    {
        var target = Normalize(app);
        return target.Length > 0 && data.AutoTypeApps.Any(a => Normalize(a) == target);
    }

    /// <summary>そのアプリに紐付けたエントリ（ゴミ箱のものは除く）。</summary>
    public static IReadOnlyList<VaultEntry> Linked(IEnumerable<VaultEntry> entries, string app) =>
        entries.Where(e => !e.Data.IsTrashed && IsLinked(e.Data, app)).ToList();

    /// <summary>アプリが開いたときの自動入力で、この題名の画面には反応しないか（題名に登録した文字を含む）。</summary>
    public static bool IsIgnoredWindow(EntryData data, string title) =>
        data.AutoTypeIgnoreTitles.Any(t => t.Trim().Length > 0 && title.Contains(t.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>打ち込む手順。Enter は押さない（ログインするかは本人が決める）。空の項目は打たない。</summary>
    public static IReadOnlyList<AutoTypeAction> Sequence(EntryData data)
    {
        var actions = new List<AutoTypeAction>();
        if (data.AutoType == AutoTypeMode.UsernameTabPassword && data.Username.Length > 0)
        {
            actions.Add(new AutoTypeAction.Text(data.Username));
            actions.Add(new AutoTypeAction.Tab());
        }
        if (data.Password.Length > 0)
            actions.Add(new AutoTypeAction.Text(data.Password));
        return actions;
    }
}

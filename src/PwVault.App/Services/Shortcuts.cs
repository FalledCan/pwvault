using Avalonia.Input;

namespace PwVault.App.Services;

/// <summary>
/// キーボードショートカットの OS による違い（Windows は Ctrl、macOS は ⌘）。
/// 画面の定義（axaml）は Ctrl で書き、macOS では起動時に ⌘ に読み替える。表示用の文字もここで作る。
/// </summary>
public static class Shortcuts
{
    private static bool Mac => OperatingSystem.IsMacOS();

    /// <summary>Ctrl で定義したショートカットを、macOS では ⌘（Meta）に置き換える。</summary>
    public static void AdaptForPlatform(InputElement element)
    {
        if (!Mac) return;
        foreach (var binding in element.KeyBindings)
        {
            if (binding.Gesture is { } g && g.KeyModifiers.HasFlag(KeyModifiers.Control))
                binding.Gesture = new KeyGesture(g.Key, (g.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
        }
    }

    /// <summary>表示用（例: Windows「Ctrl+Shift+C」、macOS「⇧⌘C」）。</summary>
    public static string Label(string key, bool shift = false) =>
        Mac ? (shift ? "⇧⌘" : "⌘") + key : "Ctrl+" + (shift ? "Shift+" : "") + key;

    public static string Search => Label("F");
    public static string New => Label("N");
    public static string Edit => Label("E");
    public static string CopyUser => Label("B");
    public static string CopyPassword => Label("C", shift: true);
    public static string Generate => Label("G");
    public static string Lock => Label("L");
    public static string Save => Label("S");

    public static string SearchPlaceholder => $"検索（タイトル・ユーザーID・URL・タグ） {Search}";
    public static string NewTip => $"新しいエントリ ({New})";
    public static string GenerateTip => $"パスワード生成 ({Generate})";
    public static string LockTip => $"ロック ({Lock})";

    public static string EmptyDetailHint =>
        $"左の一覧からエントリを選んでください\n\n{Search} 検索 ・ {New} 新規 ・ {CopyUser} ユーザーIDをコピー\n{CopyPassword} パスワードをコピー ・ {Edit} 編集 ・ {Lock} ロック";
}

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace PwVault.App.Services;

/// <summary>画面の明るさ。</summary>
public enum ThemeMode { System, Light, Dark }

/// <summary>ボタンや選択中の行などに使う色（アクセントカラー）。System は OS の設定の色。</summary>
public enum AccentColor { System, Blue, Teal, Green, Purple, Pink, Red, Orange, Graphite }

/// <summary>テーマ（明るさ・アクセントカラー）をアプリ全体に反映する。</summary>
public static class ThemeService
{
    public sealed record ThemeOption(ThemeMode Mode, string Label);

    public sealed record AccentOption(AccentColor Accent, string Label, Color Color);

    public static IReadOnlyList<ThemeOption> Modes { get; } =
    [
        new(ThemeMode.System, "OS に合わせる"),
        new(ThemeMode.Light, "ライト"),
        new(ThemeMode.Dark, "ダーク"),
    ];

    /// <summary>選べる色。System の見本の色は Windows の既定の青。</summary>
    public static IReadOnlyList<AccentOption> Accents { get; } =
    [
        new(AccentColor.System, "OS に合わせる", Color.Parse("#0078D4")),
        new(AccentColor.Blue, "青", Color.Parse("#0063B1")),
        new(AccentColor.Teal, "青緑", Color.Parse("#038387")),
        new(AccentColor.Green, "緑", Color.Parse("#107C10")),
        new(AccentColor.Purple, "紫", Color.Parse("#744DA9")),
        new(AccentColor.Pink, "ピンク", Color.Parse("#BF0077")),
        new(AccentColor.Red, "赤", Color.Parse("#C42B1C")),
        new(AccentColor.Orange, "オレンジ", Color.Parse("#CA5010")),
        new(AccentColor.Graphite, "グレー", Color.Parse("#5D5A58")),
    ];

    public static void Apply(ThemeMode mode, AccentColor accent)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } fluent) return;
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            if (accent == AccentColor.System)
                fluent.Palettes.Remove(variant); // OS のアクセントカラーに戻す
            else
                fluent.Palettes[variant] = new ColorPaletteResources { Accent = Accents.First(a => a.Accent == accent).Color };
        }
    }

    /// <summary>いま実際に暗い表示か（OS に合わせているときは OS の設定で決まる）。</summary>
    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
}

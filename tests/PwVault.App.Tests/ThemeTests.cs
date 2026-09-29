using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using PwVault.App.Services;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Crypto;

namespace PwVault.App.Tests;

/// <summary>テーマ（ライト / ダーク / OS に合わせる）と色（アクセントカラー）の切り替え。</summary>
public class ThemeTests
{
    private const string Master = "correct horse battery staple";

    private static async Task<VaultViewModel> Unlock(Harness h)
    {
        using (var v = Vault.Create(h.VaultPath, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2)))
        {
            v.AddEntry(new EntryData { Title = "銀行", Username = "taro", Password = "pw", Url = "https://bank.example" });
            v.AddEntry(new EntryData { Title = "メール", Username = "taro@example.com", Password = "pw2", Url = "https://mail.example" });
            v.Save(3);
        }
        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        return h.Page<VaultViewModel>();
    }

    private static Color AccentFor(ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource("SystemAccentColor", variant, out var value));
        return Assert.IsType<Color>(value);
    }

    [AvaloniaFact]
    public async Task ToolbarButton_TogglesLightAndDark_AndIsSaved()
    {
        using var h = new Harness(s => s.Theme = ThemeMode.Light);
        Assert.Equal(ThemeVariant.Light, Application.Current!.RequestedThemeVariant);
        var vm = await Unlock(h);
        Assert.Equal("🌙 ダーク", h.Main.ThemeToggleText);

        h.Main.ToggleThemeCommand.Execute(null);
        Assert.Equal(ThemeVariant.Dark, Application.Current.ActualThemeVariant);
        Assert.Equal(ThemeMode.Dark, h.Main.Settings.Theme);
        Assert.Equal("☀ ライト", h.Main.ThemeToggleText);
        Assert.Equal(ThemeMode.Dark, new AppSettingsStore(Path.Combine(h.Dir, "settings.json")).Load().Theme);
        vm.SelectedItem = vm.Items[0];
        h.Screenshot("23-dark-theme");

        h.Main.ToggleThemeCommand.Execute(null);
        Assert.Equal(ThemeVariant.Light, Application.Current.ActualThemeVariant);
    }

    [AvaloniaFact]
    public async Task Settings_SelectsThemeAndAccentColor()
    {
        using var h = new Harness();
        var vm = await Unlock(h);
        var systemAccent = AccentFor(ThemeVariant.Light);

        vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(vm.SubPage);
        Assert.Equal(ThemeMode.System, settings.SelectedTheme!.Mode);
        Assert.Equal(AccentColor.System, settings.SelectedAccent!.Accent);

        settings.SelectedAccent = settings.Accents.Single(a => a.Accent == AccentColor.Green);
        Assert.Equal(Color.Parse("#107C10"), AccentFor(ThemeVariant.Light));
        Assert.Equal(Color.Parse("#107C10"), AccentFor(ThemeVariant.Dark));
        Assert.Equal(AccentColor.Green, h.Main.Settings.Accent);

        settings.SelectedTheme = settings.ThemeModes.Single(m => m.Mode == ThemeMode.Dark);
        Assert.Equal(ThemeVariant.Dark, Application.Current!.ActualThemeVariant);
        h.Window.Height = 1100;
        settings.ShowCategory(SettingsCategory.Display);
        h.Screenshot("24-settings-theme-green-dark");
        h.Window.Height = 720;

        // ツールバーのボタンで切り替えると、設定画面の選択も合わせて変わる
        h.Main.ToggleThemeCommand.Execute(null);
        Assert.Equal(ThemeMode.Light, settings.SelectedTheme!.Mode);

        // 「OS に合わせる」に戻すと、OS のアクセントカラーに戻る
        settings.SelectedAccent = settings.Accents.Single(a => a.Accent == AccentColor.System);
        Assert.Equal(systemAccent, AccentFor(ThemeVariant.Light));
    }
}

using Avalonia.Headless.XUnit;
using PwVault.App.Services;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Crypto;

namespace PwVault.App.Tests;

/// <summary>チュートリアル（初回画面・保管庫の作成直後・設定から）と、設定のカテゴリー分け。</summary>
public class TutorialTests
{
    private const string Master = "correct horse battery staple";

    private static async Task<VaultViewModel> Unlock(Harness h)
    {
        using (var v = Vault.Create(h.VaultPath, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2)))
        {
            v.AddEntry(new EntryData { Title = "銀行", Username = "taro", Password = "pw", Url = "https://bank.example" });
            v.Save(3);
        }
        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        return h.Page<VaultViewModel>();
    }

    [AvaloniaFact]
    public void FromSetupScreen_WalksThroughAllSteps_AndRemembersIt()
    {
        using var h = new Harness();
        var setup = h.Page<SetupViewModel>();
        setup.Main.StartTutorialCommand.Execute(null);

        var t = Assert.IsType<TutorialViewModel>(h.Main.Tutorial);
        Assert.True(t.IsFirst);
        Assert.Equal("はじめる", t.NextText);
        Assert.False(t.CanOpenSettings); // 初回画面では設定を開けない
        h.Screenshot("27-tutorial-welcome");

        var count = t.Steps.Count;
        Assert.True(count >= 8);
        for (var i = 1; i < count; i++)
        {
            t.NextCommand.Execute(null);
            Assert.Equal(i, t.Index);
            Assert.False(t.CanOpenSettings);
        }
        Assert.True(t.IsLast);
        Assert.Equal("完了", t.NextText);
        t.BackCommand.Execute(null);
        Assert.Equal(count - 2, t.Index);
        t.NextCommand.Execute(null);
        t.NextCommand.Execute(null); // 完了

        Assert.Null(h.Main.Tutorial);
        Assert.True(h.Main.Settings.TutorialSeen);
        Assert.True(new AppSettingsStore(Path.Combine(h.Dir, "settings.json")).Load().TutorialSeen);
    }

    [AvaloniaFact]
    public async Task AfterCreatingFirstVault_OffersTutorialOnce()
    {
        using var h = new Harness();
        var setup = h.Page<SetupViewModel>();
        setup.VaultPath = Path.Combine(h.Dir, "new.pwv");
        setup.Password = setup.ConfirmPassword = "a long enough master password 42!";
        setup.Acknowledged = true;
        await setup.CreateCommand.ExecuteAsync(null);
        await Harness.WaitFor(() => h.Main.CurrentPage is VaultViewModel);

        var t = Assert.IsType<TutorialViewModel>(h.Main.Tutorial);
        t.CloseCommand.Execute(null); // スキップ
        Assert.Null(h.Main.Tutorial);
        Assert.True(h.Main.Settings.TutorialSeen);
    }

    [AvaloniaFact]
    public async Task FromSettings_OpenSettingsButtonJumpsToCategory()
    {
        using var h = new Harness();
        var vm = await Unlock(h);
        vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(vm.SubPage);
        settings.ShowCategory(SettingsCategory.Help);
        Assert.True(settings.IsHelp);
        h.Screenshot("28-settings-help");
        h.Main.StartTutorialCommand.Execute(null);

        var t = h.Main.Tutorial!;
        while (t.Current.Settings != SettingsCategory.Browser) t.NextCommand.Execute(null);
        Assert.True(t.CanOpenSettings);
        h.Screenshot("29-tutorial-browser-step");
        t.OpenSettingsCommand.Execute(null);

        Assert.Null(h.Main.Tutorial);
        var opened = Assert.IsType<SettingsViewModel>(vm.SubPage);
        Assert.True(opened.IsBrowser);
        Assert.False(opened.IsGeneral);
    }

    [AvaloniaFact]
    public async Task Lock_ClosesTutorial()
    {
        using var h = new Harness();
        await Unlock(h);
        h.Main.StartTutorialCommand.Execute(null);
        h.Main.Lock();
        Assert.Null(h.Main.Tutorial);
    }

    [AvaloniaFact]
    public async Task Settings_AreGroupedIntoCategories()
    {
        using var h = new Harness();
        var vm = await Unlock(h);
        vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(vm.SubPage);

        Assert.Equal(["一般", "表示", "セキュリティ", "保存先と同期", "ブラウザ連携", "更新", "使い方"], settings.Categories.Select(c => c.Label));
        Assert.True(settings.IsGeneral); // 最初は「一般」
        h.Screenshot("30-settings-general");

        settings.SelectedCategory = settings.Categories.Single(c => c.Category == SettingsCategory.Security);
        Assert.True(settings.IsSecurity);
        Assert.False(settings.IsGeneral);
        h.Window.Height = 1300;
        h.Screenshot("31-settings-security");
        h.Window.Height = 720;

        settings.OpenProjectPageCommand.Execute(null);
        Assert.Equal(SettingsViewModel.ProjectPage, Assert.Single(h.OpenedUrls).AbsoluteUri);
    }
}

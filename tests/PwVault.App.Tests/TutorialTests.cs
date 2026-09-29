using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using PwVault.App.Services;
using PwVault.App.ViewModels;
using PwVault.App.Views;
using PwVault.Core;
using PwVault.Core.Crypto;

namespace PwVault.App.Tests;

/// <summary>実際の画面を照らして案内するチュートリアルと、設定のカテゴリー分け。</summary>
public class TutorialTests
{
    private const string Master = "correct horse battery staple";

    private static async Task<VaultViewModel> Unlock(Harness h, bool withEntries = true)
    {
        using (var v = Vault.Create(h.VaultPath, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2)))
        {
            if (withEntries)
                v.AddEntry(new EntryData { Title = "銀行", Username = "taro", Password = "pw", Url = "https://bank.example" });
            v.Save(3);
        }
        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        return h.Page<VaultViewModel>();
    }

    private static TourOverlay Overlay(Harness h) => h.Window.GetVisualDescendants().OfType<TourOverlay>().Single();

    /// <summary>今のページを表示して、照らす位置を測る。</summary>
    private static TourOverlay Show(Harness h)
    {
        Harness.Pump();
        var overlay = Overlay(h);
        overlay.UpdatePlacement();
        Harness.Pump();
        overlay.UpdatePlacement();
        return overlay;
    }

    /// <summary>全ページを順に進め、部品を指すページでは、その部品が画面の中に見つかって照らされることを確かめる。</summary>
    private static void WalkAllSteps(Harness h, TutorialViewModel t, string screenshotPrefix, params string[] screenshotTargets)
    {
        for (var i = 0; i < t.Steps.Count; i++)
        {
            if (i > 0) t.NextCommand.Execute(null);
            Assert.Equal(i, t.Index);
            var overlay = Show(h);
            var step = t.Current;
            if (step.Target is null)
            {
                Assert.Null(overlay.CurrentTarget);
            }
            else
            {
                Assert.True(overlay.CurrentTarget is not null, $"「{step.Title}」の部品（{step.Target}）が画面に見つかりません。");
                Assert.Equal(step.Target, Tour.GetId(overlay.CurrentTarget!));
                var hole = overlay.Spotlight!.Value;
                Assert.True(hole.Width > 0 && hole.Height > 0 && hole.Bottom <= h.Window.Bounds.Height + 1, $"{step.Target}: {hole}");
            }
            if (step.Target is { } id && screenshotTargets.Contains(id))
                h.Screenshot($"{screenshotPrefix}-{id}");
        }
    }

    [AvaloniaFact]
    public void SetupScreen_TourSpotlightsEachField_AndDoesNotMarkSeen()
    {
        using var h = new Harness();
        // 2 台目の PC: 同期フォルダに保管庫がある（「ほかの PC の保管庫」も案内する）
        var cloud = Path.Combine(h.CreateGoogleDrive(), "PwVault", "vault.pwv");
        Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
        Vault.Create(cloud, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2)).Dispose();
        h.Main.ShowSetup();
        var setup = h.Page<SetupViewModel>();

        setup.Main.StartTutorialCommand.Execute(null);
        var t = Assert.IsType<TutorialViewModel>(h.Main.Tutorial);
        Assert.Contains(t.Steps, s => s.Target == "setup-cloud");
        Show(h);
        h.Screenshot("27-tour-setup-welcome");

        WalkAllSteps(h, t, "27-tour-setup", "setup-password");
        Assert.Equal("完了", t.NextText);
        t.NextCommand.Execute(null);

        Assert.Null(h.Main.Tutorial);
        Assert.False(h.Main.Settings.TutorialSeen); // 作成後に一覧の案内を出すため、まだ「見た」にしない
    }

    [AvaloniaFact]
    public async Task VaultScreen_TourSpotlightsRealControls_IncludingSettings_ThenReturnsToList()
    {
        using var h = new Harness();
        var vm = await Unlock(h);
        h.Main.StartTutorialCommand.Execute(null);
        var t = Assert.IsType<TutorialViewModel>(h.Main.Tutorial);

        string[] expected = ["new", "search", "list", "detail-password", "filter", "generate", "import", "trash", "settings",
            "settings-categories", "settings-browser", "settings-storage", "settings-hello", "lock", "theme"];
        Assert.Equal(expected, t.Steps.Select(s => s.Target).OfType<string>());

        WalkAllSteps(h, t, "29-tour", "new", "detail-password", "settings-browser", "theme");

        // 設定のページでは実際に設定画面が開いている
        t.BackCommand.Execute(null); // theme
        t.BackCommand.Execute(null); // lock
        t.BackCommand.Execute(null); // settings-hello
        Assert.True(Assert.IsType<SettingsViewModel>(vm.SubPage).IsSecurity);

        t.CloseCommand.Execute(null); // 途中でやめても、一覧に戻る
        Assert.Null(h.Main.Tutorial);
        Assert.Null(vm.SubPage);
        Assert.True(h.Main.Settings.TutorialSeen);
        Assert.True(new AppSettingsStore(Path.Combine(h.Dir, "settings.json")).Load().TutorialSeen);
    }

    [AvaloniaFact]
    public async Task VaultScreen_WithoutEntries_SkipsTheCopyStep()
    {
        using var h = new Harness();
        await Unlock(h, withEntries: false);
        h.Main.StartTutorialCommand.Execute(null);
        var t = h.Main.Tutorial!;
        Assert.DoesNotContain(t.Steps, s => s.Target == "detail-password");
        WalkAllSteps(h, t, "tour-empty");
    }

    [AvaloniaFact]
    public async Task AfterCreatingFirstVault_OffersTourOnce()
    {
        using var h = new Harness();
        var setup = h.Page<SetupViewModel>();
        setup.VaultPath = Path.Combine(h.Dir, "new.pwv");
        setup.Password = setup.ConfirmPassword = "a long enough master password 42!";
        setup.Acknowledged = true;
        await setup.CreateCommand.ExecuteAsync(null);
        await Harness.WaitFor(() => h.Main.CurrentPage is VaultViewModel);

        var t = Assert.IsType<TutorialViewModel>(h.Main.Tutorial);
        Assert.Contains(t.Steps, s => s.Target == "new"); // 一覧の画面の案内
        t.CloseCommand.Execute(null); // スキップ
        Assert.True(h.Main.Settings.TutorialSeen);
    }

    [AvaloniaFact]
    public async Task Lock_ClosesTutorial()
    {
        using var h = new Harness();
        await Unlock(h);
        h.Main.StartTutorialCommand.Execute(null);
        h.Main.Tutorial!.NextCommand.Execute(null);
        h.Main.Lock();
        Assert.Null(h.Main.Tutorial);
    }

    [Fact]
    public void BubbleIsPlacedBelowAboveOrCentered()
    {
        var area = new Size(1000, 700);
        var bubble = new Size(400, 200);
        // 上の方の部品 → 下に出す
        var (_, y1) = TourOverlay.PlaceBubble(new Rect(100, 10, 80, 30), bubble, area);
        Assert.Equal(52, y1);
        // 下の方の部品 → 上に出す
        var (_, y2) = TourOverlay.PlaceBubble(new Rect(100, 650, 80, 30), bubble, area);
        Assert.Equal(650 - 12 - 200, y2);
        // 画面の右端の部品でも、吹き出しははみ出さない
        var (x3, _) = TourOverlay.PlaceBubble(new Rect(960, 10, 30, 30), bubble, area);
        Assert.True(x3 + bubble.Width <= area.Width - 12);
        // 部品が無ければ中央
        Assert.Equal((300.0, 250.0), TourOverlay.PlaceBubble(null, bubble, area));
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

        settings.ShowCategory(SettingsCategory.Help);
        h.Screenshot("28-settings-help");
        settings.OpenProjectPageCommand.Execute(null);
        Assert.Equal(SettingsViewModel.ProjectPage, Assert.Single(h.OpenedUrls).AbsoluteUri);
    }
}

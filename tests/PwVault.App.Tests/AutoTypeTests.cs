using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PwVault.App.Services;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Crypto;

namespace PwVault.App.Tests;

/// <summary>
/// ゲーム・アプリのログイン画面への自動タイプ。OS の機能は偽物（FakeAutoType）で、本物のキー入力・ショートカットキー登録はしない。
/// </summary>
public class AutoTypeTests
{
    private const string Master = "correct horse battery staple";
    private static readonly TargetWindow Game = new(new IntPtr(0x1234), 4321, "ffxivboot.exe", "FINAL FANTASY XIV");

    private static async Task<VaultViewModel> Unlock(Harness h, params EntryData[] entries)
    {
        using (var v = Vault.Create(h.VaultPath, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2)))
        {
            foreach (var e in entries) v.AddEntry(e);
            v.Save(3);
        }
        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        return h.Page<VaultViewModel>();
    }

    private static EntryData Ff14(bool linked = true) => new()
    {
        Title = "FF14", Username = "hikari", Password = "crystal-p@ss",
        AutoTypeApps = linked ? ["ffxivboot.exe"] : [],
    };

    private static EntryData Bank() => new() { Title = "銀行", Username = "taro", Password = "bank-pw", Url = "https://bank.example" };

    private static void Enable(Harness h)
    {
        h.Main.Settings.AutoTypeEnabled = true;
        Assert.Null(h.Main.ApplyAutoTypeSettings());
    }

    private static AutoTypePickerViewModel PressHotKeyOn(Harness h, TargetWindow target)
    {
        h.AutoType.Foreground = target;
        h.AutoType.PressHotKey!();
        Harness.Pump();
        return Assert.IsType<AutoTypePickerViewModel>(h.Main.AutoTypePicker);
    }

    [AvaloniaFact]
    public async Task Settings_EnableAndChangeHotKey_RegistersIt()
    {
        using var h = new Harness();
        var vm = await Unlock(h);
        Assert.Null(h.AutoType.RegisteredKey); // 初期状態は無効（勝手にキーを奪わない）

        vm.ShowSettingsAt(SettingsCategory.AutoType);
        var settings = Assert.IsType<SettingsViewModel>(vm.SubPage);
        Assert.True(settings.IsAutoType);
        settings.AutoTypeEnabled = true;
        Assert.Equal("CtrlAltA", h.AutoType.RegisteredKey!.Id);
        Assert.Contains("Ctrl + Alt + A", settings.AutoTypeStatus);
        h.Window.Height = 900;
        h.Screenshot("32-settings-autotype");
        h.Window.Height = 720;

        settings.SelectedHotKey = AutoTypeHotKey.Presets.Single(k => k.Id == "CtrlAltP");
        Assert.Equal("CtrlAltP", h.AutoType.RegisteredKey!.Id);
        Assert.Equal("CtrlAltP", new AppSettingsStore(Path.Combine(h.Dir, "settings.json")).Load().AutoTypeHotKeyId);

        // ほかのアプリが使っているキーは登録できないと知らせる
        h.AutoType.FailRegister = true;
        settings.SelectedHotKey = AutoTypeHotKey.Presets.Single(k => k.Id == "CtrlShiftAltA");
        Assert.Contains("ほかのアプリが使っている", settings.AutoTypeStatus);

        h.AutoType.FailRegister = false;
        settings.AutoTypeEnabled = false;
        Assert.Null(h.AutoType.RegisteredKey);
    }

    [AvaloniaFact]
    public async Task LinkedEntry_IsListedFirst_AndTypesIdTabPassword()
    {
        using var h = new Harness();
        var vm = await Unlock(h, Bank(), Ff14());
        Enable(h);

        var picker = PressHotKeyOn(h, Game);
        Assert.Equal("FF14", picker.Items[0].Title); // このアプリに紐付けたものが先頭
        Assert.True(picker.Items[0].IsLinked);
        Assert.Equal(picker.Items[0], picker.SelectedItem);

        await picker.ChooseCommand.ExecuteAsync(null);

        Assert.Equal(["hikari", "<TAB>", "crystal-p@ss"], h.AutoType.Typed); // Enter は押さない
        Assert.Null(h.Main.AutoTypePicker);
        Assert.Contains("ffxivboot.exe に入力しました", vm.Status);
    }

    [AvaloniaFact]
    public async Task PickerWindow_Renders()
    {
        using var h = new Harness();
        await Unlock(h, Bank(), Ff14());
        Enable(h);
        var picker = PressHotKeyOn(h, Game);
        var window = new Views.AutoTypePickerWindow { DataContext = picker };
        window.Show();
        Harness.Pump();
        Save(window, "34-autotype-picker");

        picker.SelectedItem = picker.Items.Single(i => i.Title == "銀行");
        await picker.ChooseCommand.ExecuteAsync(null); // 紐付けていないので確認
        Harness.Pump();
        Save(window, "35-autotype-confirm");
        window.ClosingFromViewModel = true;
        window.Close();

        static void Save(Avalonia.Controls.Window w, string name)
        {
            var dir = Environment.GetEnvironmentVariable("PWVAULT_SCREENSHOT_DIR") ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
            Directory.CreateDirectory(dir);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
#pragma warning disable CS0618
            w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
#pragma warning restore CS0618
        }
    }

    [AvaloniaFact]
    public async Task UnlinkedEntry_AsksFirst_AndCanLinkIt()
    {
        using var h = new Harness();
        var vm = await Unlock(h, Ff14(linked: false));
        Enable(h);

        var picker = PressHotKeyOn(h, Game);
        await picker.ChooseCommand.ExecuteAsync(picker.Items.Single());
        Assert.NotNull(picker.Confirming); // 紐付けていないアプリには、確認してから入力する
        Assert.Empty(h.AutoType.Typed);
        Assert.Contains("ffxivboot.exe", picker.ConfirmText);

        await picker.LinkAndTypeCommand.ExecuteAsync(null);
        Assert.Equal(["hikari", "<TAB>", "crystal-p@ss"], h.AutoType.Typed);

        // 紐付けは保存され、次からは確認なしで先頭に出る
        using (var check = Vault.Open(h.VaultPath, Master))
            Assert.Equal(["ffxivboot.exe"], check.GetEntries().Single().Data.AutoTypeApps);
        var again = PressHotKeyOn(h, Game);
        Assert.True(again.Items.Single().IsLinked);
    }

    [AvaloniaFact]
    public async Task TypeOnce_DoesNotLink_AndCancelTypesNothing()
    {
        using var h = new Harness();
        await Unlock(h, Ff14(linked: false));
        Enable(h);

        var picker = PressHotKeyOn(h, Game);
        await picker.ChooseCommand.ExecuteAsync(null);
        picker.CancelConfirmCommand.Execute(null);
        Assert.Null(picker.Confirming);
        Assert.Empty(h.AutoType.Typed);

        await picker.ChooseCommand.ExecuteAsync(null);
        await picker.TypeOnceCommand.ExecuteAsync(null);
        Assert.Equal(3, h.AutoType.Typed.Count);
        using var check = Vault.Open(h.VaultPath, Master);
        Assert.Empty(check.GetEntries().Single().Data.AutoTypeApps);

        var second = PressHotKeyOn(h, Game);
        second.CancelCommand.Execute(null);
        Assert.Null(h.Main.AutoTypePicker);
    }

    [AvaloniaFact]
    public async Task ForegroundChangesWhileTyping_StopsBeforePassword()
    {
        using var h = new Harness();
        await Unlock(h, Ff14());
        Enable(h);
        var picker = PressHotKeyOn(h, Game);

        // ID を打った直後に、ほかのウィンドウ（チャットなど）が前面に出た
        h.AutoType.AfterEachInput = () => h.AutoType.Foreground = new TargetWindow(new IntPtr(0x9999), 777, "discord.exe", "Discord");
        await picker.ChooseCommand.ExecuteAsync(null);

        Assert.Equal(["hikari"], h.AutoType.Typed); // パスワードは打たない
        Assert.Contains("切り替わった", picker.Error);
        Assert.False(picker.IsTyping); // 選択窓を出し直して知らせる
        Assert.Same(picker, h.Main.AutoTypePicker);
    }

    [AvaloniaFact]
    public async Task CannotReturnToTarget_TypesNothing()
    {
        using var h = new Harness();
        await Unlock(h, Ff14());
        Enable(h);
        var picker = PressHotKeyOn(h, Game);
        h.AutoType.ActivateSucceeds = false;
        await picker.ChooseCommand.ExecuteAsync(null);
        Assert.Empty(h.AutoType.Typed);
        Assert.NotNull(picker.Error);
    }

    [AvaloniaFact]
    public async Task PasswordOnlyEntry_TypesOnlyPassword()
    {
        using var h = new Harness();
        var entry = Ff14();
        entry.AutoType = AutoTypeMode.PasswordOnly;
        await Unlock(h, entry);
        Enable(h);
        await PressHotKeyOn(h, Game).ChooseCommand.ExecuteAsync(null);
        Assert.Equal(["crystal-p@ss"], h.AutoType.Typed);
    }

    [AvaloniaFact]
    public async Task WhenLocked_AsksToUnlockInsteadOfShowingPicker()
    {
        using var h = new Harness();
        await Unlock(h, Ff14());
        Enable(h);
        h.Main.Lock();

        h.AutoType.Foreground = Game;
        h.AutoType.PressHotKey!();
        Harness.Pump();
        Assert.Null(h.Main.AutoTypePicker);
        Assert.Contains("アンロックしてから", h.Page<UnlockViewModel>().Info);
        Assert.Empty(h.AutoType.Typed);
    }

    [AvaloniaFact]
    public async Task PwVaultItselfInFront_DoesNothing_AndLockClosesPicker()
    {
        using var h = new Harness();
        await Unlock(h, Ff14());
        Enable(h);

        h.AutoType.Foreground = new TargetWindow(new IntPtr(1), Environment.ProcessId, "PwVault.exe", "PwVault");
        h.AutoType.PressHotKey!();
        Harness.Pump();
        Assert.Null(h.Main.AutoTypePicker);

        PressHotKeyOn(h, Game);
        h.Main.Lock();
        Assert.Null(h.Main.AutoTypePicker);
    }

    [AvaloniaFact]
    public async Task Search_AndEditorLinksApps()
    {
        using var h = new Harness();
        var vm = await Unlock(h, Bank(), Ff14(linked: false));
        Enable(h);
        var picker = PressHotKeyOn(h, Game);
        picker.SearchText = "銀行";
        Assert.Equal("銀行", picker.Items.Single().Title);
        picker.CancelCommand.Execute(null);

        // 編集画面で紐付ける
        vm.SelectedItem = vm.Items.Single(i => i.Title == "FF14");
        vm.EditEntryCommand.Execute(null);
        var editor = vm.Editor!;
        Assert.True(editor.AutoTypeSupported);
        editor.AutoTypeAppsText = "ffxivboot.exe, ffxiv_dx11.exe";
        editor.AutoTypePasswordOnly = true;
        h.Screenshot("33-editor-autotype");
        editor.SaveCommand.Execute(null);

        var data = vm.Vault.GetEntries().Single(e => e.Data.Title == "FF14").Data;
        Assert.Equal(["ffxivboot.exe", "ffxiv_dx11.exe"], data.AutoTypeApps);
        Assert.Equal(AutoTypeMode.PasswordOnly, data.AutoType);
        Assert.True(PressHotKeyOn(h, Game).Items[0].IsLinked);
    }
}

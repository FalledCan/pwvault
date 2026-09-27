using Avalonia.Headless.XUnit;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Crypto;

namespace PwVault.App.Tests;

/// <summary>Windows Hello でのアンロック（本物の代わりに FakeHello を使う）。</summary>
public class QuickUnlockUiTests
{
    private const string Master = "correct horse battery staple";

    private static async Task<VaultViewModel> UnlockWithPassword(Harness h)
    {
        var unlock = h.Page<UnlockViewModel>();
        await unlock.QuickUnlockReady;
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        return h.Page<VaultViewModel>();
    }

    private static async Task<Harness> Setup(bool enroll)
    {
        var h = new Harness(s => s.FetchSiteIcons = false);
        using (var v = Vault.Create(h.VaultPath, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2)))
        {
            v.AddEntry(new EntryData { Title = "Example", Password = "k#8Vq!2mZr$9Lx@4" });
            v.Save(5);
        }
        h.Main.ShowUnlock(h.VaultPath);
        if (!enroll) return h;

        var vm = await UnlockWithPassword(h);
        vm.ShowSettingsCommand.Execute(null);
        var settings = (SettingsViewModel)vm.SubPage!;
        settings.QuickUnlockPassword = Master;
        await settings.EnableQuickUnlockCommand.ExecuteAsync(null);
        Assert.True(settings.QuickUnlockEnabled, settings.QuickUnlockStatus);
        h.Main.LockCommand.Execute(null);
        return h;
    }

    [AvaloniaFact]
    public async Task Enable_RequiresMasterPassword()
    {
        using var h = await Setup(enroll: false);
        var vm = await UnlockWithPassword(h);
        vm.ShowSettingsCommand.Execute(null);
        var settings = (SettingsViewModel)vm.SubPage!;
        Assert.True(settings.QuickUnlockSupported);
        Assert.False(settings.QuickUnlockEnabled);

        settings.QuickUnlockPassword = "wrong";
        await settings.EnableQuickUnlockCommand.ExecuteAsync(null);
        Assert.False(settings.QuickUnlockEnabled);
        Assert.Contains("違います", settings.QuickUnlockStatus);
        Assert.Equal(0, h.Hello.Prompts); // パスワードが違えば OS の確認画面も出さない

        h.Hello.UserCancels = true;
        settings.QuickUnlockPassword = Master;
        await settings.EnableQuickUnlockCommand.ExecuteAsync(null);
        Assert.False(settings.QuickUnlockEnabled);
        Assert.Contains("取り消され", settings.QuickUnlockStatus);
    }

    [AvaloniaFact]
    public async Task QuickUnlock_OpensVault_WithoutMasterPassword()
    {
        using var h = await Setup(enroll: true);
        var unlock = h.Page<UnlockViewModel>();
        await unlock.QuickUnlockReady;
        Assert.True(unlock.CanQuickUnlock);

        // 画面が出ると自動で確認画面を出す。ここでは取り消して、ボタンの表示を撮る
        h.Hello.UserCancels = true;
        var prompts = h.Hello.Prompts;
        h.Screenshot("15-unlock-windows-hello");
        await Harness.WaitFor(() => h.Hello.Prompts > prompts);
        await Harness.WaitFor(() => !unlock.IsBusy);
        h.Page<UnlockViewModel>();

        h.Hello.UserCancels = false;
        await unlock.QuickUnlockCommand.ExecuteAsync(null);
        var vault = h.Page<VaultViewModel>();
        Assert.Equal("Example", Assert.Single(vault.Items).Title);
    }

    [AvaloniaFact]
    public async Task QuickUnlock_Cancelled_StaysLocked()
    {
        using var h = await Setup(enroll: true);
        h.Hello.UserCancels = true;
        var unlock = h.Page<UnlockViewModel>();
        await unlock.QuickUnlockReady;
        await unlock.QuickUnlockCommand.ExecuteAsync(null);
        Assert.Contains("取り消され", unlock.Info);
        Assert.True(unlock.CanQuickUnlock); // もう一度試せる
        h.Page<UnlockViewModel>();
    }

    [AvaloniaFact]
    public async Task QuickUnlock_Expires_AndMasterPasswordRenewsIt()
    {
        using var h = await Setup(enroll: true);
        h.Clock.Now = h.Clock.Now.AddDays(15); // 既定は 14 日

        var unlock = h.Page<UnlockViewModel>();
        await unlock.QuickUnlockReady;
        await unlock.QuickUnlockCommand.ExecuteAsync(null);
        Assert.Contains("有効期限", unlock.Error);
        Assert.False(unlock.CanQuickUnlock);
        h.Page<UnlockViewModel>();

        // マスターパスワードで開くと期限が延び、次からまた使える
        await UnlockWithPassword(h);
        h.Main.LockCommand.Execute(null);
        unlock = h.Page<UnlockViewModel>();
        await unlock.QuickUnlockReady;
        await unlock.QuickUnlockCommand.ExecuteAsync(null);
        h.Page<VaultViewModel>();
    }

    [AvaloniaFact]
    public async Task ChangingMasterPassword_RemovesEnrollment()
    {
        using var h = await Setup(enroll: true);
        var vm = await UnlockWithPassword(h);
        vm.ShowSettingsCommand.Execute(null);
        var settings = (SettingsViewModel)vm.SubPage!;
        settings.CurrentPassword = Master;
        settings.NewPassword = settings.ConfirmNewPassword = "a much better passphrase 42";
        await settings.ChangePasswordCommand.ExecuteAsync(null);
        Assert.False(settings.QuickUnlockEnabled);
        Assert.Contains("登録は解除", settings.PasswordStatus);

        h.Main.LockCommand.Execute(null);
        var unlock = h.Page<UnlockViewModel>();
        await unlock.QuickUnlockReady;
        Assert.False(unlock.CanQuickUnlock);
    }

    [AvaloniaFact]
    public async Task NotShown_WhenHelloUnavailable()
    {
        using var h = await Setup(enroll: true);
        h.Hello.Available = false;
        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        await unlock.QuickUnlockReady;
        Assert.False(unlock.CanQuickUnlock);
    }
}

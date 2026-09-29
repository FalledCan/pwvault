using Avalonia.Headless.XUnit;
using PwVault.App.Services;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Crypto;
using PwVault.Core.Storage;

namespace PwVault.App.Tests;

/// <summary>
/// 保存先の切り替え（この PC / Google ドライブ / Nextcloud）と、同期フォルダ経由でほかの PC と同じ保管庫を使うときの取り込み。
/// 同期フォルダはすべて一時フォルダの中に作る（本物の Google ドライブ・Nextcloud には触らない）。
/// </summary>
public class StorageSyncTests
{
    private const string Master = "correct horse battery staple";

    private static KdfParameters FastKdf() => KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2);

    private static EntryData Entry(string title, string password = "pw") =>
        new() { Title = title, Username = "user", Password = password, Url = "https://example.com" };

    /// <summary>保管庫を作ってアンロックし、一覧画面を返す。</summary>
    private static async Task<VaultViewModel> UnlockNew(Harness h, string path, params string[] titles)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var v = Vault.Create(path, Master, FastKdf()))
        {
            foreach (var t in titles) v.AddEntry(Entry(t));
            v.Save(3);
        }
        h.Main.ShowUnlock(path);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        return h.Page<VaultViewModel>();
    }

    private static string[] Titles(VaultViewModel vm) => vm.Items.Select(i => i.Title).Order(StringComparer.Ordinal).ToArray();

    [AvaloniaFact]
    public void Locator_FindsGoogleDriveAndNextcloud_OnlyWhenInstalled()
    {
        using var h = new Harness();
        Assert.Null(h.SyncFolders.FindGoogleDrive());
        Assert.Null(h.SyncFolders.FindNextcloud());

        var drive = h.CreateGoogleDrive();
        var nextcloud = h.CreateNextcloud();
        Assert.Equal(drive, h.SyncFolders.FindGoogleDrive());
        Assert.Equal(nextcloud, h.SyncFolders.FindNextcloud());

        Assert.Equal(StorageKind.GoogleDrive, h.SyncFolders.KindOf(Path.Combine(drive, "PwVault", "vault.pwv")));
        Assert.Equal(StorageKind.Nextcloud, h.SyncFolders.KindOf(Path.Combine(nextcloud, "PwVault", "vault.pwv")));
        Assert.Equal(StorageKind.Local, h.SyncFolders.KindOf(Path.Combine(h.SyncFolders.LocalFolder, "vault.pwv")));
        Assert.Equal(StorageKind.Other, h.SyncFolders.KindOf(h.VaultPath));
    }

    [AvaloniaFact]
    public async Task MoveFromThisPc_ToGoogleDrive_AndBackToThisPc()
    {
        using var h = new Harness();
        var drive = h.CreateGoogleDrive();
        var local = Path.Combine(h.SyncFolders.LocalFolder, "vault.pwv");
        var vm = await UnlockNew(h, local, "銀行");
        vm.Vault.AddEntry(Entry("メール"));
        vm.Persist(); // .bak1 ができる

        vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(vm.SubPage);
        Assert.Equal("この PC", settings.CurrentStorage);
        Assert.True(settings.CanMoveToGoogleDrive);
        Assert.False(settings.CanMoveToNextcloud); // Nextcloud は入っていない
        h.Window.Height = 1000;
        h.Screenshot("20-storage-settings");

        var move = settings.MoveToGoogleDriveCommand.ExecuteAsync(null);
        Assert.NotNull(h.Main.Confirm);
        h.Main.ConfirmOkCommand.Execute(null);
        await move;

        var cloud = Path.Combine(drive, "PwVault", "vault.pwv");
        Assert.Equal(cloud, vm.Vault.FilePath);
        Assert.Equal(cloud, h.Main.Settings.VaultPath);
        Assert.Equal("Google ドライブ", settings.CurrentStorage);
        Assert.True(File.Exists(cloud));
        Assert.True(File.Exists(AtomicFileStore.BackupPath(cloud, 1)));
        Assert.False(File.Exists(local));                               // この PC からの移動なので元は消す
        Assert.False(File.Exists(AtomicFileStore.BackupPath(local, 1)));
        using (var check = Vault.Open(cloud, Master))
            Assert.Equal(2, check.GetEntries().Count);

        // この PC に戻すときは、ほかの PC が使っているかもしれないのでクラウドのファイルは残す
        move = settings.MoveToLocalCommand.ExecuteAsync(null);
        h.Main.ConfirmOkCommand.Execute(null);
        await move;
        Assert.Equal(Path.GetFullPath(local), vm.Vault.FilePath);
        Assert.True(File.Exists(local));
        Assert.True(File.Exists(cloud));
        Assert.Contains("残してあります", settings.StorageStatus);
        h.Window.Height = 720;
    }

    [AvaloniaFact]
    public async Task ChangesFromAnotherPc_AreMergedIntoTheOpenVault()
    {
        using var h = new Harness();
        var cloud = Path.Combine(h.CreateNextcloud(), "PwVault", "vault.pwv");
        var vm = await UnlockNew(h, cloud, "共通");

        // もう 1 台の PC（同じファイルを開いた別のインスタンス）が追加・完全削除する
        using (var otherPc = Vault.Open(cloud, Master))
        {
            otherPc.AddEntry(Entry("ほかの PC で追加"));
            otherPc.Save(3);
        }
        // その間にこちらでも追加していた（保存の直前に取り込むので、どちらも残る）
        vm.Vault.AddEntry(Entry("この PC で追加"));
        Assert.True(vm.Persist());

        vm.SyncNow();
        Assert.Equal(["この PC で追加", "ほかの PC で追加", "共通"], Titles(vm));

        // 同期アプリが作った競合コピーも取り込んで消す
        var copy = Path.Combine(Path.GetDirectoryName(cloud)!, "vault (conflicted copy 2026-09-29 120000).pwv");
        File.Copy(cloud, copy);
        using (var otherPc = Vault.Open(copy, Master))
        {
            otherPc.AddEntry(Entry("競合コピー側"));
            otherPc.Save(0);
        }
        vm.SyncNow();
        Assert.Contains("競合コピー側", Titles(vm));
        Assert.Equal("他の端末での変更を取り込みました。", vm.Status);
        Assert.False(File.Exists(copy));
        h.Screenshot("21-synced-from-other-pc");

        using var reopened = Vault.Open(cloud, Master);
        Assert.Equal(4, reopened.GetEntries().Count);
    }

    [AvaloniaFact]
    public async Task EditingAnEntryPurgedOnAnotherPc_SavesItAsNew()
    {
        using var h = new Harness();
        var vm = await UnlockNew(h, h.VaultPath, "消されるもの");
        vm.SelectedItem = vm.Items.Single();
        vm.EditEntryCommand.Execute(null);
        var editor = vm.Editor!;

        using (var otherPc = Vault.Open(h.VaultPath, Master))
        {
            otherPc.Purge(otherPc.GetEntries().Single().Id);
            otherPc.Save(3);
        }
        vm.SyncNow();
        Assert.Empty(vm.Items);

        editor.EntryTitle = "編集した内容";
        editor.SaveCommand.Execute(null);
        Assert.Equal(["編集した内容"], Titles(vm));
        Assert.Contains("新しいエントリとして保存しました", vm.Status);
    }

    [AvaloniaFact]
    public async Task TrashPage_ItemPurgedOnAnotherPc_DoesNotCrash()
    {
        using var h = new Harness();
        var vm = await UnlockNew(h, h.VaultPath, "捨てたもの", "もう 1 つ");
        foreach (var item in vm.Items.ToList()) vm.Vault.MoveToTrash(item.Id);
        vm.Persist();
        vm.ShowTrashCommand.Execute(null);
        var trash = Assert.IsType<TrashViewModel>(vm.SubPage);
        Assert.Equal(2, trash.Items.Count);
        var stale = trash.Items.Single(i => i.Title == "捨てたもの");

        // ゴミ箱を開いている間に、ほかの PC が「捨てたもの」を完全削除した
        using (var otherPc = Vault.Open(h.VaultPath, Master))
        {
            otherPc.Purge(otherPc.GetEntries().Single(e => e.Data.Title == "捨てたもの").Id);
            otherPc.Save(3);
        }
        vm.SyncNow();
        Assert.Single(trash.Items); // 取り込んだらゴミ箱の一覧も更新される

        // 古い一覧の項目を押しても落ちない
        trash.RestoreCommand.Execute(stale);
        Assert.Contains("ほかの端末で削除", trash.Status);
        var purge = trash.PurgeCommand.ExecuteAsync(stale);
        await purge;
        Assert.Contains("ほかの端末で削除", trash.Status);
    }

    [AvaloniaFact]
    public async Task Startup_VaultInSyncFolderNotReadyYet_ShowsUnlockAndWaits()
    {
        // サインイン直後など、同期フォルダ（Google ドライブの G: など）がまだ準備できていない
        var cloud = Path.Combine(Path.GetTempPath(), "pwvault-notready-" + Guid.NewGuid().ToString("N"), "PwVault", "vault.pwv");
        using var h = new Harness(s => s.VaultPath = cloud);

        // 初回セットアップ（新しい保管庫の作成）ではなく、いつものアンロック画面で待つ
        var unlock = h.Page<UnlockViewModel>();
        Assert.Equal(cloud, unlock.VaultPath);
        Assert.Contains("見つかりません", unlock.Error);
        h.Screenshot("25-unlock-waiting-for-sync-folder");

        // 同期フォルダが準備できてファイルが現れたら、案内が消えてアンロックできる
        Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
        Vault.Create(cloud, Master, FastKdf()).Dispose();
        try
        {
            unlock.CheckVaultFile();
            Assert.Null(unlock.Error);
            unlock.Password = Master;
            await unlock.UnlockCommand.ExecuteAsync(null);
            h.Page<VaultViewModel>();
        }
        finally
        {
            h.Main.Lock();
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(cloud))!, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Lock_WhenSaveKeepsFailing_RescuesChangesInsteadOfDroppingThem()
    {
        using var h = new Harness();
        var vm = await UnlockNew(h, h.VaultPath, "元からある");
        var good = File.ReadAllBytes(h.VaultPath);

        // ファイルが読めない状態（改ざん・壊れた同期など）になり、保存が失敗し続ける
        var doc = Core.Format.VaultFileCodec.Deserialize(good);
        var e = doc.Entries[0];
        var forged = doc with { Entries = [new Core.Format.EncryptedEntry { Id = e.Id, Revision = e.Revision + 9, UpdatedAt = e.UpdatedAt, Deleted = e.Deleted, Box = e.Box }] };
        File.WriteAllBytes(h.VaultPath, Core.Format.VaultFileCodec.Serialize(forged));

        vm.Vault.AddEntry(Entry("ロック直前に追加"));
        Assert.False(vm.Persist());
        h.Main.Lock(); // 自動ロックでも同じ

        var rescue = Assert.Single(Directory.GetFiles(h.Dir, "vault (保存できなかった変更 *).pwv"));
        var unlock = h.Page<UnlockViewModel>();
        Assert.Contains("退避しました", unlock.Error);
        Assert.Contains(rescue, unlock.Error);
        using (var rescued = Vault.Open(rescue, Master))
            Assert.Contains(rescued.GetEntries(), x => x.Data.Title == "ロック直前に追加");

        // 保管庫ファイルを .bak などから戻して開き直すと、退避した変更は自動で取り込まれる
        File.WriteAllBytes(h.VaultPath, good);
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        vm = h.Page<VaultViewModel>();
        Assert.Equal(["ロック直前に追加", "元からある"], Titles(vm));
        Assert.False(File.Exists(rescue));
    }

    [AvaloniaFact]
    public async Task SetupOnSecondPc_OffersVaultFoundInSyncFolder()
    {
        using var h = new Harness();
        var cloud = Path.Combine(h.CreateGoogleDrive(), "PwVault", "vault.pwv");
        Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
        Vault.Create(cloud, Master, FastKdf()).Dispose();

        h.Main.ShowSetup();
        var setup = h.Page<SetupViewModel>();
        var found = Assert.Single(setup.CloudVaults);
        Assert.Equal("Google ドライブの保管庫を開く", found.Label);
        h.Screenshot("22-setup-cloud-vault");

        setup.OpenCloudVaultCommand.Execute(found);
        var unlock = h.Page<UnlockViewModel>();
        Assert.Equal(cloud, unlock.VaultPath);
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        h.Page<VaultViewModel>();
        Assert.Equal(cloud, h.Main.Settings.VaultPath);
    }

    [AvaloniaFact]
    public async Task MovingOntoADifferentVault_OffersToOpenItInstead()
    {
        using var h = new Harness();
        var drive = h.CreateGoogleDrive();
        var theirs = Path.Combine(drive, "PwVault", "vault.pwv");
        Directory.CreateDirectory(Path.GetDirectoryName(theirs)!);
        Vault.Create(theirs, Master, FastKdf()).Dispose(); // ほかの PC で別に作った保管庫

        var vm = await UnlockNew(h, h.VaultPath, "こちらの保管庫");
        var move = vm.MoveVaultToAsync(theirs);
        Assert.NotNull(h.Main.Confirm);
        h.Main.ConfirmOkCommand.Execute(null);
        await move;

        Assert.Equal(theirs, h.Page<UnlockViewModel>().VaultPath);
        using var mine = Vault.Open(h.VaultPath, Master); // 今の保管庫はそのまま残る
        Assert.Single(mine.GetEntries());
    }
}

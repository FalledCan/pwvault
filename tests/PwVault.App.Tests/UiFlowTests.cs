using Avalonia.Headless.XUnit;
using PwVault.App.ViewModels;
using PwVault.Core;
using PwVault.Core.Crypto;
using PwVault.Core.Storage;

namespace PwVault.App.Tests;

/// <summary>画面遷移（要件 8）を ViewModel 経由でたどり、各画面を実際に描画して確かめる。</summary>
public class UiFlowTests
{
    private const string Master = "correct horse battery staple";

    /// <summary>テストを速くするため、軽い KDF で保管庫を作っておく。</summary>
    private static void CreateVault(string path, Action<Vault>? fill = null)
    {
        using var vault = Vault.Create(path, Master, KdfParameters.CreateNew(KdfParameters.MinRecommendedMemoryKiB, 2));
        fill?.Invoke(vault);
        vault.Save(5);
    }

    private static void AddSamples(Vault v)
    {
        v.AddEntry(new EntryData { Title = "GitHub", Username = "alice", Password = "k#8Vq!2mZr$9Lx@4", Url = "https://github.com", Tags = ["仕事"], Favorite = true });
        v.AddEntry(new EntryData { Title = "銀行", Username = "1234567", Password = "password1", Url = "https://bank.example", Notes = "暗証番号は別管理", Tags = ["お金"] });
        v.AddEntry(new EntryData { Title = "メール", Username = "alice@example.com", Password = "Shared#Secret#2026!", Url = "https://mail.example", Tags = ["仕事"] });
        v.AddEntry(new EntryData { Title = "ショッピング", Username = "alice@example.com", Password = "Shared#Secret#2026!", Url = "https://shop.example" });
    }

    [AvaloniaFact]
    public async Task FirstRun_ShowsSetup_AndCreatesVault()
    {
        using var h = new Harness();
        var setup = h.Page<SetupViewModel>();
        setup.VaultPath = h.VaultPath;
        h.Screenshot("01-setup");

        // 短すぎる・不一致・未確認はエラー
        setup.Password = "short";
        await setup.CreateCommand.ExecuteAsync(null);
        Assert.NotNull(setup.Error);

        setup.Password = "Tr0ub4dor&3-horse-battery";
        setup.ConfirmPassword = "mismatch";
        setup.Acknowledged = true;
        await setup.CreateCommand.ExecuteAsync(null);
        Assert.Contains("一致", setup.Error);

        setup.ConfirmPassword = setup.Password;
        h.Screenshot("01-setup-filled");
        await setup.CreateCommand.ExecuteAsync(null);

        var vault = h.Page<VaultViewModel>();
        Assert.True(File.Exists(h.VaultPath));
        Assert.Empty(vault.Items);
        Assert.Equal(h.VaultPath, h.Main.Settings.VaultPath);
        h.Screenshot("02-empty-vault");
    }

    [AvaloniaFact]
    public async Task Unlock_WrongPasswordClearsField_ThenUnlocks()
    {
        using var h = new Harness(s => s.VaultPath = Path.Combine(Path.GetTempPath(), "placeholder"));
        CreateVault(h.VaultPath, AddSamples);
        h.Main.ShowUnlock(h.VaultPath);

        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = "wrong password";
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.Equal("", unlock.Password);           // FR-02
        Assert.Contains("違います", unlock.Error);
        Assert.False(unlock.ShowBackupHelp);
        h.Screenshot("03-unlock-error");

        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        var vault = h.Page<VaultViewModel>();
        Assert.Equal(4, vault.Items.Count);
        Assert.Equal("GitHub", vault.Items[0].Title); // お気に入りが先頭
    }

    [AvaloniaFact]
    public async Task EntryList_Search_Detail_Edit_History()
    {
        using var h = new Harness();
        CreateVault(h.VaultPath, AddSamples);
        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        var vm = h.Page<VaultViewModel>();

        // 詳細表示（パスワードは既定で伏せ字、FR-09）
        vm.SelectedItem = vm.Items.First(i => i.Title == "銀行");
        Assert.False(vm.RevealPassword);
        Assert.True(vm.SelectedItem.IsWeak);
        h.Screenshot("04-detail");

        // 検索（FR-06）
        vm.SearchText = "alice@";
        Assert.Equal(2, vm.Items.Count);
        vm.SearchText = "";

        // 健全性フィルタ（FR-14）
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Reused);
        Assert.Equal(["ショッピング", "メール"], vm.Items.Select(i => i.Title).Order());
        vm.SelectedFilter = vm.Filters.First(f => f.Kind == FilterKind.Tag && f.Tag == "仕事");
        Assert.Equal(2, vm.Items.Count);
        vm.SelectedFilter = vm.Filters[0];

        // 編集（S5）→ パスワード生成（S6）→ 採用 → 保存
        vm.SelectedItem = vm.Items.First(i => i.Title == "銀行");
        vm.EditEntryCommand.Execute(null);
        var editor = Assert.IsType<EntryEditorViewModel>(vm.Editor);
        h.Screenshot("05-editor");
        editor.GenerateCommand.Execute(null);
        var gen = Assert.IsType<GeneratorViewModel>(vm.Generator);
        gen.Length = 24;
        h.Screenshot("06-generator");
        var generated = gen.Generated;
        gen.AcceptCommand.Execute(null);
        Assert.Null(vm.Generator);
        Assert.Equal(generated, editor.Password);
        editor.SaveCommand.Execute(null);

        Assert.Null(vm.Editor);
        Assert.Equal("銀行", vm.SelectedItem?.Title);
        Assert.Equal(generated, vm.SelectedItem!.Entry.Data.Password);
        Assert.Equal("password1", Assert.Single(vm.SelectedItem.Entry.Data.History).Password); // FR-15
        Assert.False(vm.SelectedItem.IsWeak);

        // 新規追加（Ctrl+N 相当）
        vm.NewEntryCommand.Execute(null);
        vm.Editor!.EntryTitle = "新しいサイト";
        vm.Editor.Password = "abc";
        vm.Editor.SaveCommand.Execute(null);
        Assert.Equal("新しいサイト", vm.SelectedItem?.Title);
        Assert.Equal(5, vm.Items.Count);

        // 保存されていることを、別インスタンスで開いて確かめる
        using var reopened = Vault.Open(h.VaultPath, Master);
        Assert.Equal(5, reopened.GetEntries().Count);
    }

    [AvaloniaFact]
    public async Task Trash_Restore_Purge()
    {
        using var h = new Harness();
        CreateVault(h.VaultPath, AddSamples);
        h.Main.ShowUnlock(h.VaultPath);
        h.Page<UnlockViewModel>().Password = Master;
        await h.Page<UnlockViewModel>().UnlockCommand.ExecuteAsync(null);
        var vm = h.Page<VaultViewModel>();

        vm.SelectedItem = vm.Items.First(i => i.Title == "銀行");
        vm.TrashEntryCommand.Execute(null);
        vm.SelectedItem = vm.Items.First(i => i.Title == "メール");
        vm.TrashEntryCommand.Execute(null);
        Assert.Equal(2, vm.Items.Count);

        vm.ShowTrashCommand.Execute(null);
        var trash = Assert.IsType<TrashViewModel>(vm.SubPage);
        Assert.Equal(2, trash.Items.Count);
        h.Screenshot("07-trash");

        trash.RestoreCommand.Execute(trash.Items.First(i => i.Title == "銀行"));
        var purge = trash.PurgeCommand.ExecuteAsync(trash.Items.Single());
        Assert.NotNull(h.Main.Confirm);     // 完全削除は確認を挟む
        h.Screenshot("08-confirm");
        h.Main.ConfirmOkCommand.Execute(null);
        await purge;
        Assert.True(trash.IsEmpty);

        vm.CloseSubPageCommand.Execute(null);
        Assert.Equal(3, vm.Items.Count);
        using var reopened = Vault.Open(h.VaultPath, Master);
        Assert.Equal(3, reopened.GetEntries().Count);
    }

    [AvaloniaFact]
    public async Task Lock_Manual_And_Auto_ReturnToUnlock()
    {
        using var h = new Harness();
        CreateVault(h.VaultPath, AddSamples);
        h.Main.ShowUnlock(h.VaultPath);
        h.Page<UnlockViewModel>().Password = Master;
        await h.Page<UnlockViewModel>().UnlockCommand.ExecuteAsync(null);
        var vm = h.Page<VaultViewModel>();
        vm.ShowSettingsCommand.Execute(null);

        // どの画面からでもロックでアンロック画面へ
        h.Main.LockCommand.Execute(null);
        h.Page<UnlockViewModel>();
        Assert.Empty(vm.Items);
        Assert.Throws<InvalidOperationException>(() => vm.Vault);
        h.Screenshot("09-locked");

        // 自動ロック（無操作）
        h.Page<UnlockViewModel>().Password = Master;
        await h.Page<UnlockViewModel>().UnlockCommand.ExecuteAsync(null);
        h.Page<VaultViewModel>();
        h.AutoLock.IdleTimeout = TimeSpan.Zero;
        h.AutoLock.CheckIdle();
        h.Page<UnlockViewModel>();
    }

    [AvaloniaFact]
    public async Task CorruptedVault_OffersBackupRestore()
    {
        using var h = new Harness();
        CreateVault(h.VaultPath, AddSamples);
        using (var v = Vault.Open(h.VaultPath, Master))
        {
            v.AddEntry(new EntryData { Title = "追加" });
            v.Save(5); // これで .bak1 ができる
        }
        var text = File.ReadAllText(h.VaultPath);
        File.WriteAllText(h.VaultPath, text[..(text.Length / 2)]);

        h.Main.ShowUnlock(h.VaultPath);
        var unlock = h.Page<UnlockViewModel>();
        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.True(unlock.ShowBackupHelp);
        Assert.Equal(2, unlock.Backups.Count); // 作成後の保存と追加後の保存で 2 世代
        var backup = unlock.Backups[0];        // .bak1 = 直前の版（4 件）
        h.Screenshot("10-corrupted");

        var restore = unlock.RestoreBackupCommand.ExecuteAsync(backup);
        h.Main.ConfirmOkCommand.Execute(null);
        await restore;
        Assert.Single(Directory.GetFiles(h.Dir, "vault.pwv.corrupt-*"));

        unlock.Password = Master;
        await unlock.UnlockCommand.ExecuteAsync(null);
        Assert.Equal(4, h.Page<VaultViewModel>().Items.Count);
    }

    [AvaloniaFact]
    public async Task Settings_ChangeMasterPassword_AndGeneral()
    {
        using var h = new Harness();
        CreateVault(h.VaultPath);
        h.Main.ShowUnlock(h.VaultPath);
        h.Page<UnlockViewModel>().Password = Master;
        await h.Page<UnlockViewModel>().UnlockCommand.ExecuteAsync(null);
        var vm = h.Page<VaultViewModel>();
        vm.ShowSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsViewModel>(vm.SubPage);
        h.Screenshot("11-settings");

        settings.AutoLockMinutes = 10;
        settings.ClipboardSeconds = 45;
        settings.SaveGeneralCommand.Execute(null);
        Assert.Equal(10, h.Main.Settings.AutoLockMinutes);
        Assert.Equal(TimeSpan.FromMinutes(10), h.AutoLock.IdleTimeout);

        settings.CurrentPassword = "wrong";
        settings.NewPassword = settings.ConfirmNewPassword = "a much better passphrase 42";
        await settings.ChangePasswordCommand.ExecuteAsync(null);
        Assert.Contains("違います", settings.PasswordStatus);

        settings.CurrentPassword = Master;
        settings.NewPassword = settings.ConfirmNewPassword = "a much better passphrase 42";
        await settings.ChangePasswordCommand.ExecuteAsync(null);
        Assert.Contains("変更しました", settings.PasswordStatus);

        h.Main.LockCommand.Execute(null);
        Assert.Throws<VaultException>(() => Vault.Open(h.VaultPath, Master).Dispose());
        Vault.Open(h.VaultPath, "a much better passphrase 42").Dispose();
    }

    [AvaloniaFact]
    public async Task ImportCsv_AndPlainExportRequiresReauth()
    {
        using var h = new Harness();
        CreateVault(h.VaultPath);
        h.Main.ShowUnlock(h.VaultPath);
        h.Page<UnlockViewModel>().Password = Master;
        await h.Page<UnlockViewModel>().UnlockCommand.ExecuteAsync(null);
        var vm = h.Page<VaultViewModel>();
        vm.ShowImportExportCommand.Execute(null);
        var io = Assert.IsType<ImportExportViewModel>(vm.SubPage);
        h.Screenshot("12-import-export");

        var csv = Path.Combine(h.Dir, "chrome.csv");
        File.WriteAllText(csv, "name,url,username,password,note\nGitHub,https://github.com,alice,pw1,\nMail,https://mail.example,bob,pw2,\n");
        h.Dialogs.Next.Enqueue(csv);
        var import = io.ImportCommand.ExecuteAsync(null);
        await Harness.WaitFor(() => h.Main.Confirm is not null);
        h.Main.ConfirmOkCommand.Execute(null);
        await import;
        Assert.Contains("2 件", io.ImportStatus);

        // 同じ CSV をもう一度取り込んでも重複しない
        h.Dialogs.Next.Enqueue(csv);
        await io.ImportCommand.ExecuteAsync(null);
        Assert.Contains("取り込む件数: 0 件", io.ImportStatus);

        // 平文エクスポート: 同意なし・パスワード誤りは拒否（SR-12）
        var outCsv = Path.Combine(h.Dir, "out.csv");
        io.CsvPassword = Master;
        await io.ExportCsvCommand.ExecuteAsync(null);
        Assert.False(File.Exists(outCsv));
        io.CsvAcknowledged = true;
        io.CsvPassword = "wrong";
        await io.ExportCsvCommand.ExecuteAsync(null);
        Assert.Contains("違います", io.CsvStatus);
        Assert.Equal("", io.CsvPassword);

        io.CsvPassword = Master;
        h.Dialogs.Next.Enqueue(outCsv);
        await io.ExportCsvCommand.ExecuteAsync(null);
        Assert.Contains("pw2", File.ReadAllText(outCsv));

        // 暗号化バックアップは同じパスワードで開ける
        var bak = Path.Combine(h.Dir, "backup.pwv");
        h.Dialogs.Next.Enqueue(bak);
        await io.ExportBackupCommand.ExecuteAsync(null);
        using var backup = Vault.Open(bak, Master);
        Assert.Equal(2, backup.GetEntries().Count);
        Assert.Empty(AtomicFileStore.ListBackups(bak));
    }
}

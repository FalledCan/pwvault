using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core;
using PwVault.Core.QuickUnlock;
using PwVault.Core.Storage;

namespace PwVault.App.ViewModels;

public sealed record BackupItem(string Path, DateTime SavedAt)
{
    public string Display => $"{System.IO.Path.GetFileName(Path)}  （{SavedAt:yyyy/MM/dd HH:mm:ss} 保存）";
}

/// <summary>S2 アンロック（FR-02, NFR-06）。</summary>
public partial class UnlockViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public UnlockViewModel(MainViewModel main, string vaultPath)
    {
        _main = main;
        VaultPath = vaultPath;
        QuickUnlockReady = InitQuickUnlockAsync();
        if (!File.Exists(vaultPath))
        {
            Error = MissingFileMessage;
            _fileWatch = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _fileWatch.Tick += (_, _) => CheckVaultFile();
            _fileWatch.Start();
        }
        // ロック時に保存できなかった変更を退避した、など（専用の欄に出し、打ち間違えても消さない）
        if (main.LockNotice is { } notice)
        {
            Notice = notice;
            main.LockNotice = null;
        }
    }

    public string VaultPath { get; }

    private const string MissingFileMessage =
        "保管庫ファイルが見つかりません。Google ドライブなどの同期フォルダに置いている場合は、準備ができるまで少しお待ちください（見つかりしだいアンロックできます）。" +
        "\n移動・削除した場合は「別の保管庫を開く…」から選んでください。";

    private Avalonia.Threading.DispatcherTimer? _fileWatch;

    /// <summary>見つからなかった保管庫ファイルが現れたか確かめる（2 秒ごと）。現れたら案内を消し、Windows Hello を使えるようにする。</summary>
    public void CheckVaultFile()
    {
        if (_main.CurrentPage != this) { _fileWatch?.Stop(); return; } // 別の画面に移った
        if (!File.Exists(VaultPath)) return;
        _fileWatch?.Stop();
        if (Error == MissingFileMessage) Error = null;
        _ = InitQuickUnlockAsync().ContinueWith(_ => TryAutoQuickUnlock(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ------------------------------------------------------------------ Windows Hello

    private QuickUnlockRecord? _quickRecord;

    /// <summary>Windows Hello でアンロックできるか（登録済みで、この PC で使える）。</summary>
    [ObservableProperty]
    public partial bool CanQuickUnlock { get; set; }

    public string QuickUnlockLabel => $"{_main.QuickUnlock?.Name ?? "生体認証"} でアンロック";

    /// <summary>使えるかどうかの確認が終わったら完了する（View が自動で確認画面を出すのに使う）。</summary>
    public Task QuickUnlockReady { get; }

    private async Task InitQuickUnlockAsync()
    {
        if (_main.QuickUnlock is not { } provider) return;
        try
        {
            // 保管庫 ID はヘッダ（平文）から読める。パスワードは不要
            if (new FileInfo(VaultPath).Length > Vault.MaxFileBytes) return; // 巨大なファイルは読まない（アンロック時に案内する）
            var vaultId =Core.Format.VaultFileCodec.Deserialize(await File.ReadAllBytesAsync(VaultPath)).Header.VaultId;
            _quickRecord = QuickUnlockService.Load(_main.QuickUnlockPath(vaultId));
            CanQuickUnlock = _quickRecord?.VaultId == vaultId && await provider.IsAvailableAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
        {
            CanQuickUnlock = false; // 読めない保管庫はマスターパスワードでのアンロック時に案内する
        }
    }

    private DateTimeOffset _lastAutoPrompt = DateTimeOffset.MinValue;

    /// <summary>
    /// 画面が出たとき・ウィンドウが前に出たときに、Windows Hello の確認を自動で出す。
    /// 取り消した直後にウィンドウを行き来するたび出ないよう、10 秒は間を空ける。
    /// </summary>
    public void TryAutoQuickUnlock()
    {
        if (!CanQuickUnlock || IsBusy) return;
        var now = _main.Clock.GetUtcNow();
        if (now - _lastAutoPrompt < TimeSpan.FromSeconds(10)) return;
        _lastAutoPrompt = now;
        QuickUnlockCommand.Execute(null);
    }

    [RelayCommand]
    private async Task QuickUnlockAsync()
    {
        if (IsBusy || !CanQuickUnlock || _quickRecord is not { } record || _main.QuickUnlock is not { } provider) return;
        Error = Info = null;
        IsBusy = true;
        try
        {
            var signature = await provider.SignAsync(record.VaultId, record.Challenge, create: false, _main.OwnerWindowHandle());
            if (signature is null)
            {
                Info = $"{provider.Name} での確認が取り消されました。マスターパスワードでもアンロックできます。";
                return;
            }
            var now = _main.Clock.GetUtcNow();
            var vault = await Task.Run(() => QuickUnlockService.Unlock(VaultPath, record, signature, now));
            Password = "";
            _main.OnUnlocked(vault, viaMasterPassword: false);
        }
        catch (VaultException ex)
        {
            Error = ex.Message;
            if (ex.Kind == VaultErrorKind.QuickUnlockUnavailable)
                CanQuickUnlock = false;
            FocusPasswordRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Info { get; set; }

    /// <summary>ロックしたときの事情（退避した変更の場所など）。パスワードを打ち間違えても消さずに出し続ける。</summary>
    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool ShowBackupHelp { get; set; }

    public ObservableCollection<BackupItem> Backups { get; } = [];

    /// <summary>パスワード欄にフォーカスを戻してほしいとき（View が購読する）。</summary>
    public event EventHandler? FocusPasswordRequested;

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (IsBusy || Password.Length == 0) return;
        Error = Info = null;
        IsBusy = true;
        var password = Password;
        try
        {
            var vault = await Task.Run(() => Vault.Open(VaultPath, password));
            Password = "";
            _main.OnUnlocked(vault);
        }
        catch (VaultException ex)
        {
            Password = ""; // FR-02: 誤りの場合は入力欄をクリア
            Error = ex.Message;
            if (ex.SuggestsBackupRestore)
            {
                Error += "\nバックアップから復元できます。";
                LoadBackups();
            }
            FocusPasswordRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadBackups()
    {
        Backups.Clear();
        foreach (var path in AtomicFileStore.ListBackups(VaultPath))
            Backups.Add(new BackupItem(path, File.GetLastWriteTime(path)));
        ShowBackupHelp = true;
    }

    /// <summary>
    /// バックアップから復元する。壊れた現在のファイルは消さずに .corrupt-日時 として残す。
    /// 復元後、通常どおりマスターパスワードでアンロックする。
    /// </summary>
    [RelayCommand]
    private async Task RestoreBackupAsync(BackupItem? item)
    {
        if (item is null) return;
        if (!await _main.ConfirmAsync("バックアップから復元",
                $"{Path.GetFileName(item.Path)} で保管庫を置き換えます。\n現在のファイルは「.corrupt-日時」という名前で残します。",
                "復元する"))
            return;

        try
        {
            var corruptPath = $"{VaultPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            if (File.Exists(VaultPath))
                File.Move(VaultPath, corruptPath);
            File.Copy(item.Path, VaultPath);
            ShowBackupHelp = false;
            Error = null;
            Info = "バックアップから復元しました。マスターパスワードを入力してください。";
            FocusPasswordRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "復元できませんでした。ファイルが他のアプリで開かれていないか確認してください。";
        }
    }

    [RelayCommand]
    private async Task OpenOtherAsync()
    {
        var path = await _main.FileDialogs.OpenFileAsync("保管庫を開く", "PwVault 保管庫", "*.pwv", "*.pwv.bak*");
        if (path is not null) _main.ShowUnlock(path);
    }

    [RelayCommand]
    private void CreateNew() => _main.ShowSetup();
}

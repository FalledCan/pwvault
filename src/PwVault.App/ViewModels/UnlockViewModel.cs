using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core;
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
    }

    public string VaultPath { get; }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Info { get; set; }

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

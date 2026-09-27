using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.App.Services;
using PwVault.Core;

namespace PwVault.App.ViewModels;

/// <summary>確認ダイアログの内容。</summary>
public sealed class ConfirmRequest(string title, string message, string okText, bool isDanger)
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public string Title { get; } = title;
    public string Message { get; } = message;
    public string OkText { get; } = okText;
    public bool IsDanger { get; } = isDanger;
    public Task<bool> Result => _tcs.Task;

    public void Complete(bool ok) => _tcs.TrySetResult(ok);
}

/// <summary>
/// 画面遷移の中心（要件 8 の状態遷移）。初回セットアップ／アンロック／保管庫画面を切り替え、
/// 手動・自動ロックでどの画面からでもアンロック画面へ戻す。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public AppSettings Settings { get; }
    public AppSettingsStore SettingsStore { get; }
    public ClipboardService Clipboard { get; }
    public AutoLockService AutoLock { get; }
    public IFileDialogs FileDialogs { get; }

    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    [ObservableProperty]
    public partial ConfirmRequest? Confirm { get; set; }

    public MainViewModel(AppSettingsStore store, ClipboardService clipboard, AutoLockService autoLock, IFileDialogs fileDialogs)
    {
        SettingsStore = store;
        Settings = store.Load();
        Clipboard = clipboard;
        AutoLock = autoLock;
        FileDialogs = fileDialogs;
        AutoLock.IdleMinutes = Settings.AutoLockMinutes;
        AutoLock.LockRequested += (_, _) => Lock();

        CurrentPage = Settings.VaultPath is { } path && File.Exists(path)
            ? new UnlockViewModel(this, path)
            : new SetupViewModel(this);
    }

    public void SaveSettings()
    {
        try { SettingsStore.Save(Settings); }
        catch (IOException) { /* 設定の保存失敗は致命的ではない */ }
    }

    public void ShowSetup() => CurrentPage = new SetupViewModel(this);

    public void ShowUnlock(string path)
    {
        Settings.VaultPath = path;
        SaveSettings();
        CurrentPage = new UnlockViewModel(this, path);
    }

    public void OnUnlocked(Vault vault)
    {
        Settings.VaultPath = vault.FilePath;
        SaveSettings();
        AutoLock.NotifyActivity();
        AutoLock.IsArmed = true;
        CurrentPage = new VaultViewModel(this, vault);
    }

    public bool IsUnlocked => CurrentPage is VaultViewModel;

    /// <summary>ロック（FR-03）。鍵を破棄し、アプリがコピーしたクリップボードの中身も消す。</summary>
    [RelayCommand]
    public void Lock()
    {
        AutoLock.IsArmed = false;
        Confirm?.Complete(false);
        Confirm = null;
        Clipboard.ClearIfOwned();

        if (CurrentPage is VaultViewModel vault)
        {
            var path = vault.Close();
            CurrentPage = new UnlockViewModel(this, path);
        }
    }

    /// <summary>アプリ終了時の後始末。</summary>
    public void Shutdown()
    {
        Lock();
        Clipboard.Dispose();
        AutoLock.Dispose();
    }

    public async Task<bool> ConfirmAsync(string title, string message, string okText = "OK", bool isDanger = false)
    {
        Confirm?.Complete(false);
        var request = new ConfirmRequest(title, message, okText, isDanger);
        Confirm = request;
        try
        {
            return await request.Result;
        }
        finally
        {
            if (Confirm == request) Confirm = null;
        }
    }

    [RelayCommand]
    private void ConfirmOk() => Confirm?.Complete(true);

    [RelayCommand]
    private void ConfirmCancel() => Confirm?.Complete(false);
}

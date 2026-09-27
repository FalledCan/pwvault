using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.App.Bridge;
using PwVault.App.Services;
using PwVault.Core;
using PwVault.Core.Bridge;

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

    public MainViewModel(AppSettingsStore store, ClipboardService clipboard, AutoLockService autoLock, IFileDialogs fileDialogs,
        BrowserIntegration? browserIntegration = null, string? bridgePipeName = null)
    {
        SettingsStore = store;
        Settings = store.Load();
        Clipboard = clipboard;
        AutoLock = autoLock;
        FileDialogs = fileDialogs;
        AutoLock.IdleMinutes = Settings.AutoLockMinutes;
        AutoLock.LockRequested += (_, _) => Lock();
        BrowserIntegration = browserIntegration ?? (OperatingSystem.IsWindows() ? new BrowserIntegration() : null);
        _bridgePipeName = bridgePipeName;

        CurrentPage = Settings.VaultPath is { } path && File.Exists(path)
            ? new UnlockViewModel(this, path)
            : new SetupViewModel(this);

        if (Settings.BrowserIntegration)
            ResumeBrowserIntegration();
    }

    // ------------------------------------------------------------------ ブラウザ連携

    private readonly string? _bridgePipeName;
    private BridgeServer? _bridge;

    public BrowserIntegration? BrowserIntegration { get; }

    public bool IsBridgeRunning => _bridge is not null;

    /// <summary>ブラウザ連携を有効にする: レジストリ登録・拡張機能の展開・窓口の起動。</summary>
    public void EnableBrowserIntegration()
    {
        if (BrowserIntegration is null || !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ブラウザ連携は Windows 専用です。");

        BrowserIntegration.Register(ExePath);
        Settings.BrowserIntegration = true;
        SaveSettings();
        StartBridge();
    }

    public void DisableBrowserIntegration()
    {
        StopBridge();
        if (OperatingSystem.IsWindows())
            BrowserIntegration?.Unregister();
        Settings.BrowserIntegration = false;
        SaveSettings();
    }

    private void ResumeBrowserIntegration()
    {
        // exe を移動していたら登録し直す（ブラウザが古い場所の exe を起動しようとして失敗しないように）
        if (OperatingSystem.IsWindows() && BrowserIntegration?.GetStatus(ExePath) != IntegrationStatus.Registered)
        {
            try { BrowserIntegration?.Register(ExePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        StartBridge();
    }

    public static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所が分かりません。");

    private void StartBridge() => _bridge ??= new BridgeServer(HandleBridgeRequestAsync, _bridgePipeName);

    private void StopBridge()
    {
        _bridge?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _bridge = null;
    }

    /// <summary>保管庫は UI スレッドでしか触らないので、要求の処理は UI スレッドで行う。ロック中は null を渡して「ロック中」と答える。</summary>
    internal Task<BridgeResponse> HandleBridgeRequestAsync(BridgeRequest? request) =>
        Dispatcher.UIThread.InvokeAsync(() =>
            BridgeHandler.Handle(request, CurrentPage is VaultViewModel { IsUnlocked: true } vault ? vault.Vault.GetEntries() : null)).GetTask();

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
        StopBridge();
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

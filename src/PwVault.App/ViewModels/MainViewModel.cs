using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.App.Bridge;
using PwVault.App.Services;
using PwVault.Core;
using PwVault.Core.Bridge;
using PwVault.Core.QuickUnlock;

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
        BrowserIntegration? browserIntegration = null, string? bridgePipeName = null, FaviconFetcher? iconFetcher = null,
        UpdateService? updates = null,
        IQuickUnlockProvider? quickUnlock = null, string? localDataDir = null, TimeProvider? clock = null)
    {
        QuickUnlock = quickUnlock ?? QuickUnlockProviders.CreateDefault();
        LocalDataDir = localDataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PwVault");
        Clock = clock ?? TimeProvider.System;
        IconFetcher = iconFetcher ?? new FaviconFetcher();
        Updates = updates ?? new UpdateService();
        SettingsStore = store;
        Settings = store.Load();
        Clipboard = clipboard;
        AutoLock = autoLock;
        FileDialogs = fileDialogs;
        AutoLock.IdleMinutes = Settings.AutoLockMinutes;
        AutoLock.LockRequested += (_, _) => Lock();
        BrowserIntegration = browserIntegration ?? (BrowserIntegration.IsSupported ? new BrowserIntegration() : null);
        _bridgePipeName = bridgePipeName;

        CurrentPage = Settings.VaultPath is { } path && File.Exists(path)
            ? new UnlockViewModel(this, path)
            : new SetupViewModel(this);

        if (Settings.BrowserIntegration)
            ResumeBrowserIntegration();
    }

    // ------------------------------------------------------------------ Windows Hello（クイックアンロック）

    public IQuickUnlockProvider? QuickUnlock { get; }

    /// <summary>この端末だけに置くデータ（クイックアンロックの登録など）の場所。</summary>
    public string LocalDataDir { get; }

    public TimeProvider Clock { get; }

    /// <summary>OS の確認画面の親にするウィンドウ。</summary>
    public Func<IntPtr> OwnerWindowHandle { get; set; } = () => IntPtr.Zero;

    public string QuickUnlockPath(Guid vaultId) => Path.Combine(LocalDataDir, "QuickUnlock", vaultId.ToString("N") + ".json");

    public bool IsQuickUnlockEnrolled(Guid vaultId) => QuickUnlockService.Load(QuickUnlockPath(vaultId)) is { } r && r.VaultId == vaultId;

    /// <summary>
    /// Windows Hello を登録する。マスターパスワードを確かめてから、OS の確認（顔・指紋・PIN）を経て鍵を作る。
    /// 失敗したら理由を返す。
    /// </summary>
    public async Task<string?> EnableQuickUnlockAsync(Vault vault, string masterPassword)
    {
        if (QuickUnlock is null || !await QuickUnlock.IsAvailableAsync())
            return "この PC では Windows Hello を使えません（Windows の設定で顔認証・指紋・PIN を設定してください）。";
        if (!await Task.Run(() => vault.VerifyPassword(masterPassword)))
            return "マスターパスワードが違います。";

        var challenge = QuickUnlockService.NewChallenge();
        var signature = await QuickUnlock.SignAsync(vault.VaultId, challenge, create: true, OwnerWindowHandle());
        if (signature is null)
            return $"{QuickUnlock.Name} での確認が取り消されました。";

        try
        {
            var record = QuickUnlockService.Enroll(vault, challenge, signature, Clock.GetUtcNow().AddDays(Settings.QuickUnlockDays));
            QuickUnlockService.Save(QuickUnlockPath(vault.VaultId), record);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "登録を保存できませんでした。";
        }
    }

    public async Task DisableQuickUnlockAsync(Guid vaultId)
    {
        try { File.Delete(QuickUnlockPath(vaultId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (QuickUnlock is not null) await QuickUnlock.DeleteAsync(vaultId);
    }

    /// <summary>マスターパスワードでアンロックしたら、Windows Hello の期限を延ばす（OS の確認は不要）。</summary>
    private void RefreshQuickUnlockExpiry(Vault vault)
    {
        var path = QuickUnlockPath(vault.VaultId);
        if (QuickUnlockService.Load(path) is not { } record || record.VaultId != vault.VaultId) return;
        try
        {
            QuickUnlockService.Save(path, QuickUnlockService.RefreshExpiry(vault, record, Clock.GetUtcNow().AddDays(Settings.QuickUnlockDays)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ 更新

    public UpdateService Updates { get; }

    public string CurrentVersion => UpdateService.CurrentVersion;

    /// <summary>見つかった新しい版（画面上部に通知を出す）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial UpdateInfo? AvailableUpdate { get; set; }

    public bool HasUpdate => AvailableUpdate is not null;

    [ObservableProperty] public partial string? UpdateStatus { get; set; }
    [ObservableProperty] public partial bool IsUpdating { get; set; }
    [ObservableProperty] public partial double UpdateProgress { get; set; }

    /// <summary>更新を入れたので再起動してほしい（App が受けてアプリを終了する）。</summary>
    public event EventHandler? RestartRequested;

    private DispatcherTimer? _updateTimer;

    /// <summary>設定でオンなら、起動の少し後と 1 日ごとに更新を確認する。</summary>
    public void StartUpdateChecks()
    {
        if (_updateTimer is not null) return;
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (Settings.CheckForUpdates)
                await CheckForUpdatesAsync(silent: true);
        };
        _updateTimer.Start();
    }

    [RelayCommand]
    private Task CheckForUpdatesNow() => CheckForUpdatesAsync(silent: false);

    /// <param name="silent">自動確認のときは、「最新です」や通信エラーを表示しない。</param>
    public async Task CheckForUpdatesAsync(bool silent)
    {
        if (!silent) UpdateStatus = "確認中…";
        try
        {
            AvailableUpdate = await Updates.CheckAsync(CurrentVersion);
            if (!silent)
                UpdateStatus = AvailableUpdate is null ? $"最新の版です（v{CurrentVersion}）。" : $"v{AvailableUpdate.Version} が利用できます。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UpdateException)
        {
            if (!silent) UpdateStatus = "更新を確認できませんでした（ネットワークに接続できません）。";
        }
    }

    /// <summary>更新をダウンロードし、署名を確かめて入れ替え、再起動する。自動で入れ替えられない OS ではリリースページを開く。</summary>
    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } update || IsUpdating) return;
        if (!update.CanInstall || !OperatingSystem.IsWindows())
        {
            OpenInBrowser(new Uri(update.PageUrl));
            return;
        }

        IsUpdating = true;
        UpdateStatus = "ダウンロード中…";
        try
        {
            var exe = ExePath;
            var progress = new Progress<double>(p => UpdateProgress = p);
            var file = await Updates.DownloadAndVerifyAsync(update, Path.GetDirectoryName(exe)!, progress);

            UpdateStatus = "再起動しています…";
            Lock(); // 未保存の変更を書き込み、鍵を捨ててから入れ替える
            UpdateService.ReplaceExecutable(file, exe);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, Program.AfterUpdateArgument) { UseShellExecute = false });
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UpdateException or IOException or UnauthorizedAccessException)
        {
            UpdateStatus = ex is UpdateException ? ex.Message : "更新できませんでした: " + ex.Message;
            IsUpdating = false;
        }
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (AvailableUpdate is { } update) OpenInBrowser(new Uri(update.PageUrl));
    }

    [RelayCommand]
    private void DismissUpdate()
    {
        AvailableUpdate = null;
        UpdateStatus = null;
    }

    // ------------------------------------------------------------------ ブラウザ連携

    private readonly string? _bridgePipeName;
    private BridgeServer? _bridge;

    public BrowserIntegration? BrowserIntegration { get; }

    public bool IsBridgeRunning => _bridge is not null;

    /// <summary>ブラウザ連携を有効にする: レジストリ登録・拡張機能の展開・窓口の起動。</summary>
    public void EnableBrowserIntegration()
    {
        if (BrowserIntegration is null)
            throw new PlatformNotSupportedException("ブラウザ連携はこの OS には対応していません。");

        BrowserIntegration.Register(ExePath);
        Settings.BrowserIntegration = true;
        SaveSettings();
        StartBridge();
    }

    public void DisableBrowserIntegration()
    {
        StopBridge();
        BrowserIntegration?.Unregister();
        Settings.BrowserIntegration = false;
        SaveSettings();
    }

    private void ResumeBrowserIntegration()
    {
        // exe を移動していたら登録し直す（ブラウザが古い場所の exe を起動しようとして失敗しないように）
        if (BrowserIntegration is not null && BrowserIntegration.GetStatus(ExePath) != IntegrationStatus.Registered)
        {
            try { BrowserIntegration.Register(ExePath); }
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
        {
            var vault = CurrentPage as VaultViewModel is { IsUnlocked: true } v ? v : null;
            return BridgeHandler.Handle(request, vault?.Vault.GetEntries(), (host, data) => vault?.Icons.StoreFromBrowser(host, data));
        }).GetTask();

    /// <summary>サイトからアイコンを取得する部品（テストでは通信しないものに差し替える）。</summary>
    public FaviconFetcher IconFetcher { get; }

    /// <summary>既定のブラウザで URL を開く（テストでは実際に開かないものに差し替える）。</summary>
    public Action<Uri> OpenInBrowser { get; set; } = uri =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

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

    /// <param name="viaMasterPassword">マスターパスワードで開いたか（Windows Hello の期限を延ばすのはこのときだけ）。</param>
    public void OnUnlocked(Vault vault, bool viaMasterPassword = true)
    {
        if (viaMasterPassword) RefreshQuickUnlockExpiry(vault);
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

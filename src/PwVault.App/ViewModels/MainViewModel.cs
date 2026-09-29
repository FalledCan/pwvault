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
    [NotifyPropertyChangedFor(nameof(IsUnlocked))]
    public partial ViewModelBase? CurrentPage { get; set; }

    [ObservableProperty]
    public partial ConfirmRequest? Confirm { get; set; }

    public MainViewModel(AppSettingsStore store, ClipboardService clipboard, AutoLockService autoLock, IFileDialogs fileDialogs,
        BrowserIntegration? browserIntegration = null, string? bridgePipeName = null, FaviconFetcher? iconFetcher = null,
        UpdateService? updates = null,
        IQuickUnlockProvider? quickUnlock = null, string? localDataDir = null, TimeProvider? clock = null,
        AutoStart? autoStart = null, SyncFolderLocator? syncFolders = null)
    {
        AutoStart = autoStart ?? (AutoStart.IsSupported ? new AutoStart() : null);
        SyncFolders = syncFolders ?? new SyncFolderLocator();
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
        ThemeService.Apply(Settings.Theme, Settings.Accent);
        if (Avalonia.Application.Current is { } app)
            app.ActualThemeVariantChanged += OnActualThemeChanged;

        // 前回の保管庫の場所があれば、ファイルが見つからなくてもアンロック画面にする
        // （同期フォルダの準備がまだで一時的に見えないだけのことがある。初回画面で別の保管庫を作らせない）
        CurrentPage = Settings.VaultPath is { Length: > 0 } path
            ? new UnlockViewModel(this, path)
            : new SetupViewModel(this);

        if (Settings.BrowserIntegration)
            ResumeBrowserIntegration();
        RepairAutoStart();
    }

    // ------------------------------------------------------------------ チュートリアル

    /// <summary>表示中のチュートリアル（無ければ null）。画面の上に重ねて出す。</summary>
    [ObservableProperty]
    public partial TutorialViewModel? Tutorial { get; set; }

    [RelayCommand]
    public void StartTutorial() => Tutorial = new TutorialViewModel(this);

    /// <param name="markSeen">
    /// 見たことを記録するか。初回画面の案内では記録しない（保管庫を作った直後に、一覧の画面の案内を出すため）。
    /// </param>
    public void CloseTutorial(bool markSeen = true)
    {
        Tutorial = null;
        if (markSeen && !Settings.TutorialSeen)
        {
            Settings.TutorialSeen = true;
            SaveSettings();
        }
    }

    // ------------------------------------------------------------------ テーマ

    /// <summary>ツールバーの切り替えボタンの表示（押したらなる方）。</summary>
    public string ThemeToggleText => ThemeService.IsDark ? "☀ ライト" : "🌙 ダーク";

    /// <summary>ツールバーのボタン: ライトとダークを切り替える（OS に合わせていた場合も、いまの逆にする）。</summary>
    [RelayCommand]
    private void ToggleTheme() => SetTheme(ThemeService.IsDark ? ThemeMode.Light : ThemeMode.Dark, Settings.Accent);

    public void SetTheme(ThemeMode mode, AccentColor accent)
    {
        Settings.Theme = mode;
        Settings.Accent = accent;
        SaveSettings();
        ThemeService.Apply(mode, accent);
        OnPropertyChanged(nameof(ThemeToggleText));
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    // OS に合わせているときに OS 側でライト / ダークが変わった
    private void OnActualThemeChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ThemeToggleText));

    /// <summary>テーマが変わった（設定画面の選択表示を合わせる）。</summary>
    public event EventHandler? ThemeChanged;

    // ------------------------------------------------------------------ 保存先（クラウドの同期フォルダ）

    /// <summary>Google ドライブ・Nextcloud の同期フォルダを探す部品（テストでは一時フォルダを見るものに差し替える）。</summary>
    public SyncFolderLocator SyncFolders { get; }

    /// <summary>アンロック中に、他の端末による保管庫ファイルの変更を確認する間隔。</summary>
    public TimeSpan VaultSyncInterval { get; set; } = TimeSpan.FromSeconds(3);

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

    /// <summary>ウィンドウを前に出してほしい（App が受けて表示する）。</summary>
    public event EventHandler? ShowRequested;

    // ------------------------------------------------------------------ 自動起動

    public AutoStart? AutoStart { get; }

    public bool IsAutoStartEnabled => AutoStart?.IsEnabled() == true;

    public void SetAutoStart(bool enabled)
    {
        if (AutoStart is null) return;
        if (enabled) AutoStart.Enable(ExePath);
        else AutoStart.Disable();
    }

    /// <summary>自動起動の登録が別の場所の実行ファイルを指していたら（移動した等）、今の場所に直す。</summary>
    private void RepairAutoStart()
    {
        if (AutoStart is null || !AutoStart.IsEnabled() || AutoStart.IsEnabledFor(ExePath)) return;
        try { AutoStart.Enable(ExePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
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
        // アプリを更新したら、展開済みの拡張機能のファイルも新しい版にしておく（拡張はそれを見て自分を読み込み直す）
        try { BrowserIntegration?.EnsureExtensionUpToDate(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
            // ブラウザの「PwVault をアンロックする」から: ウィンドウを前に出す（ロック中ならアンロック画面）
            if (request?.Type == BridgeRequest.TypeOpen)
                ShowRequested?.Invoke(this, EventArgs.Empty);

            var vault = CurrentPage as VaultViewModel is { IsUnlocked: true } v ? v : null;
            var response = BridgeHandler.Handle(request, vault?.Vault.GetEntries(), (host, data) => vault?.Icons.StoreFromBrowser(host, data));
            response.ExtensionVersion = BrowserIntegration.BundledExtensionVersion;
            return response;
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

    /// <summary>ロック時に伝えたいこと（保存できなかった変更の退避先など）。次のアンロック画面が表示して消す。</summary>
    public string? LockNotice { get; set; }

    /// <summary>ロック画面で伝えることを足す（複数あれば改行でつなぐ）。</summary>
    public void AddLockNotice(string message) => LockNotice = LockNotice is null ? message : LockNotice + "\n\n" + message;

    // ------------------------------------------------------------------ 想定外のエラー

    private bool _handlingUnexpected;

    /// <summary>
    /// 想定外の例外（UI の処理で捕まえていないもの）の受け皿。アプリを落とさず、変更を保存（できなければ退避）してロックする。
    /// 調査用に、例外の種類と呼び出し位置だけを記録する（メッセージは機密を含みうるので書かない）。
    /// 受け皿の中でさらに失敗したら false（呼び出し側はそのまま落とす）。
    /// </summary>
    public bool HandleUnexpectedError(Exception ex)
    {
        if (_handlingUnexpected) return false;
        _handlingUnexpected = true;
        try
        {
            WriteErrorLog(ex);
            if (IsUnlocked)
            {
                AddLockNotice("問題が起きたため、安全のためロックしました（変更は保存または退避しています）。");
                Lock();
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            _handlingUnexpected = false;
        }
    }

    /// <summary>エラーの記録の場所（この PC だけ）。</summary>
    public string ErrorLogPath => Path.Combine(LocalDataDir, "Logs", "errors.log");

    /// <summary>エラーを記録だけする（ロックはしない。バックグラウンドの処理の失敗など）。</summary>
    public void WriteErrorLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            var text = new System.Text.StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] v{CurrentVersion}");
            for (var e = ex; e is not null; e = e.InnerException)
                text.AppendLine(e.GetType().FullName).AppendLine(e.StackTrace);
            var log = new FileInfo(ErrorLogPath);
            if (log.Exists && log.Length > 1024 * 1024) log.Delete(); // 大きくなりすぎたら作り直す
            File.AppendAllText(ErrorLogPath, text.AppendLine().ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

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
            Tutorial = null; // 「この設定を開く」などは保管庫を開いている前提なので閉じる
            var path = vault.Close();
            CurrentPage = new UnlockViewModel(this, path);
        }
    }

    /// <summary>アプリ終了時の後始末。</summary>
    public void Shutdown()
    {
        if (Avalonia.Application.Current is { } app)
            app.ActualThemeVariantChanged -= OnActualThemeChanged;
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

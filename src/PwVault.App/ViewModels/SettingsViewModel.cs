using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core;
using PwVault.Core.Crypto;
using PwVault.Core.Tools;

namespace PwVault.App.ViewModels;

/// <summary>S7 設定（FR-10, FR-13）。自動ロック・クリップボード・バックアップ世代・KDF・マスターパスワード変更。</summary>
public partial class SettingsViewModel : ViewModelBase
{
    public const int MaxMemoryMiB = 1024;
    public const int MaxIterations = 32;

    private readonly VaultViewModel _owner;

    public SettingsViewModel(VaultViewModel owner)
    {
        _owner = owner;
        var s = owner.Main.Settings;
        AutoLockMinutes = s.AutoLockMinutes;
        ClipboardSeconds = s.ClipboardClearSeconds;
        BackupGenerations = s.BackupGenerations;
        MemoryMiB = owner.Vault.Kdf.MemoryKiB / 1024;
        Iterations = owner.Vault.Kdf.Iterations;
        UpdateKdfCurrent();
        UpdateBrowserStatus();
    }

    public string VaultPath => _owner.Vault.FilePath;
    public int MinMemoryMiB => KdfParameters.MinRecommendedMemoryKiB / 1024;
    public int MinIterations => KdfParameters.MinRecommendedIterations;
    public int MaxMemory => MaxMemoryMiB;
    public int MaxIter => MaxIterations;

    // ---- 一般
    [ObservableProperty] public partial decimal? AutoLockMinutes { get; set; }
    [ObservableProperty] public partial decimal? ClipboardSeconds { get; set; }
    [ObservableProperty] public partial decimal? BackupGenerations { get; set; }
    [ObservableProperty] public partial string? GeneralStatus { get; set; }

    // ---- KDF
    [ObservableProperty] public partial decimal? MemoryMiB { get; set; }
    [ObservableProperty] public partial decimal? Iterations { get; set; }
    [ObservableProperty] public partial string KdfCurrent { get; set; } = "";
    [ObservableProperty] public partial string? KdfStatus { get; set; }
    [ObservableProperty] public partial string KdfPassword { get; set; } = "";
    [ObservableProperty] public partial bool KdfBusy { get; set; }

    // ---- マスターパスワード変更
    [ObservableProperty] public partial string CurrentPassword { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewStrengthText), nameof(NewStrengthValue))]
    public partial string NewPassword { get; set; } = "";

    [ObservableProperty] public partial string ConfirmNewPassword { get; set; } = "";
    [ObservableProperty] public partial string? PasswordStatus { get; set; }
    [ObservableProperty] public partial bool PasswordBusy { get; set; }

    public int NewStrengthValue => NewPassword.Length == 0 ? 0 : (int)PasswordStrength.Evaluate(NewPassword).Level + 1;

    public string NewStrengthText
    {
        get
        {
            if (NewPassword.Length == 0) return "";
            var s = PasswordStrength.Evaluate(NewPassword);
            return $"強度: {s.Label}" + (s.Warnings.Count > 0 ? "\n・" + string.Join("\n・", s.Warnings) : "");
        }
    }

    [RelayCommand]
    private void SaveGeneral()
    {
        var s = _owner.Main.Settings;
        s.AutoLockMinutes = (int)Math.Clamp(AutoLockMinutes ?? 5, 1, 240);
        s.ClipboardClearSeconds = (int)Math.Clamp(ClipboardSeconds ?? 30, 5, 600);
        s.BackupGenerations = (int)Math.Clamp(BackupGenerations ?? 5, 0, Core.Storage.AtomicFileStore.MaxBackupGenerations);
        _owner.Main.AutoLock.IdleMinutes = s.AutoLockMinutes;
        _owner.Main.SaveSettings();
        GeneralStatus = "保存しました。";
    }

    [RelayCommand]
    private async Task MeasureKdfAsync()
    {
        if (!TryGetKdfInput(out var memKiB, out var iterations)) return;
        KdfBusy = true;
        KdfStatus = "計測中…";
        try
        {
            var elapsed = await Task.Run(() => KdfCalibrator.Measure(memKiB, iterations));
            KdfStatus = $"この設定でのアンロック所要時間: 約 {elapsed.TotalSeconds:0.00} 秒" +
                        (elapsed.TotalSeconds is < 0.5 or > 2 ? "（目安は 0.5〜2 秒）" : "");
        }
        finally { KdfBusy = false; }
    }

    [RelayCommand]
    private async Task RecommendKdfAsync()
    {
        if (!TryGetKdfInput(out var memKiB, out _)) return;
        KdfBusy = true;
        KdfStatus = "計測中…";
        try
        {
            // 設定画面ではユーザーが選んだメモリ量を固定し、反復回数だけを合わせる
            var kdf = await Task.Run(() => KdfCalibrator.Calibrate(memKiB, memKiB));
            Iterations = kdf.Iterations;
            KdfStatus = $"この端末で約 0.75 秒になる反復回数は {kdf.Iterations} 回です。「適用」で保管庫に反映します。";
        }
        finally { KdfBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyKdfAsync()
    {
        if (!TryGetKdfInput(out var memKiB, out var iterations)) return;
        if (KdfPassword.Length == 0) { KdfStatus = "マスターパスワードを入力してください。"; return; }

        KdfBusy = true;
        KdfStatus = "適用中…";
        var password = KdfPassword;
        try
        {
            await Task.Run(() => _owner.Vault.ChangeMasterPassword(password, password, memKiB, iterations));
            KdfPassword = "";
            KdfStatus = _owner.Persist() ? "KDF パラメータを更新しました。" : _owner.Status;
            UpdateKdfCurrent();
        }
        catch (VaultException ex)
        {
            KdfPassword = "";
            KdfStatus = ex.Message;
        }
        finally { KdfBusy = false; }
    }

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        PasswordStatus = null;
        if (!PasswordStrength.MeetsMasterPasswordMinimum(NewPassword))
        { PasswordStatus = $"新しいマスターパスワードは {PasswordStrength.MasterPasswordMinLength} 文字以上にしてください。"; return; }
        if (NewPassword != ConfirmNewPassword)
        { PasswordStatus = "確認用のパスワードが一致しません。"; return; }
        if (PasswordStrength.Evaluate(NewPassword).IsWeak && !await _owner.Main.ConfirmAsync("弱いマスターパスワード",
                "新しいマスターパスワードは推測されやすい可能性があります。このまま変更しますか？", "このまま変更", isDanger: true))
            return;

        PasswordBusy = true;
        var (current, next) = (CurrentPassword, NewPassword);
        try
        {
            await Task.Run(() => _owner.Vault.ChangeMasterPassword(current, next));
            CurrentPassword = NewPassword = ConfirmNewPassword = "";
            PasswordStatus = _owner.Persist()
                ? "マスターパスワードを変更しました。緊急キットの記入内容も更新してください。"
                : _owner.Status;
            UpdateKdfCurrent();
        }
        catch (VaultException ex)
        {
            CurrentPassword = "";
            PasswordStatus = ex.Message;
        }
        finally { PasswordBusy = false; }
    }

    // ---- ブラウザ連携

    [ObservableProperty] public partial string BrowserStatus { get; set; } = "";
    [ObservableProperty] public partial bool BrowserEnabled { get; set; }
    [ObservableProperty] public partial string? BrowserMessage { get; set; }

    public bool BrowserSupported => _owner.Main.BrowserIntegration is not null;
    public string ChromiumExtensionDir => _owner.Main.BrowserIntegration?.ChromiumExtensionDir ?? "";
    public string FirefoxExtensionDir => _owner.Main.BrowserIntegration?.FirefoxExtensionDir ?? "";

    private void UpdateBrowserStatus()
    {
        BrowserEnabled = _owner.Main.Settings.BrowserIntegration;
        BrowserStatus = !BrowserSupported ? "この OS では使えません"
            : BrowserEnabled ? "有効（Chrome・Edge・Firefox に登録済み）"
            : "無効";
    }

    [RelayCommand]
    private void EnableBrowser()
    {
        try
        {
            _owner.Main.EnableBrowserIntegration();
            BrowserMessage = "有効にしました。下の手順で、各ブラウザに拡張機能を読み込んでください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            BrowserMessage = "有効にできませんでした: " + ex.Message;
        }
        UpdateBrowserStatus();
    }

    [RelayCommand]
    private void DisableBrowser()
    {
        try
        {
            _owner.Main.DisableBrowserIntegration();
            BrowserMessage = "無効にしました。各ブラウザの拡張機能も不要なら削除してください。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            BrowserMessage = "無効にできませんでした: " + ex.Message;
        }
        UpdateBrowserStatus();
    }

    [RelayCommand]
    private void OpenChromiumFolder() => OpenFolder(ChromiumExtensionDir);

    [RelayCommand]
    private void OpenFirefoxFolder() => OpenFolder(FirefoxExtensionDir);

    private void OpenFolder(string dir)
    {
        if (!Directory.Exists(dir))
        {
            BrowserMessage = "先に「有効にする」を押してください。";
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void Back() => _owner.CloseSubPage();

    private bool TryGetKdfInput(out int memKiB, out int iterations)
    {
        memKiB = (int)(MemoryMiB ?? 0) * 1024;
        iterations = (int)(Iterations ?? 0);
        if (memKiB < KdfParameters.MinRecommendedMemoryKiB || memKiB > MaxMemoryMiB * 1024
            || iterations < KdfParameters.MinRecommendedIterations || iterations > MaxIterations)
        {
            KdfStatus = $"メモリは {MinMemoryMiB}〜{MaxMemoryMiB} MiB、反復回数は {MinIterations}〜{MaxIterations} の範囲で指定してください。";
            return false;
        }
        return true;
    }

    private void UpdateKdfCurrent()
    {
        var k = _owner.Vault.Kdf;
        KdfCurrent = $"現在: Argon2id / メモリ {k.MemoryKiB / 1024} MiB / 反復 {k.Iterations} 回 / 並列度 {k.Parallelism}";
    }
}

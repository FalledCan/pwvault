using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core;
using PwVault.Core.Crypto;
using PwVault.Core.Tools;

namespace PwVault.App.ViewModels;

public sealed record CloudVault(string Label, string Path);

/// <summary>S1 初回セットアップ（FR-01）。保存先・マスターパスワード・緊急キットの案内。</summary>
public partial class SetupViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public SetupViewModel(MainViewModel main)
    {
        _main = main;
        VaultPath = Path.Combine(main.SyncFolders.LocalFolder, "vault.pwv");
        CloudVaults = main.SyncFolders.FindExistingVaults()
            .Select(v => new CloudVault($"{Services.SyncFolderLocator.DisplayName(v.Kind)}の保管庫を開く", v.Path))
            .ToList();
    }

    /// <summary>同期フォルダ（Google ドライブ・Nextcloud）に既にある保管庫。2 台目の PC ではここから開く。</summary>
    public IReadOnlyList<CloudVault> CloudVaults { get; }

    public bool HasCloudVaults => CloudVaults.Count > 0;

    public MainViewModel Main => _main;

    [RelayCommand]
    private void OpenCloudVault(CloudVault? vault)
    {
        if (vault is not null) _main.ShowUnlock(vault.Path);
    }

    [ObservableProperty]
    public partial string VaultPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Strength), nameof(StrengthValue), nameof(StrengthText), nameof(IsTooShort))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = "";

    [ObservableProperty]
    public partial bool Acknowledged { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public StrengthResult Strength => PasswordStrength.Evaluate(Password);
    public int StrengthValue => Password.Length == 0 ? 0 : (int)Strength.Level + 1;
    public string StrengthText => Password.Length == 0
        ? ""
        : $"強度: {Strength.Label}（約 {Strength.EntropyBits:0} ビット）" +
          (Strength.Warnings.Count > 0 ? "\n・" + string.Join("\n・", Strength.Warnings) : "");
    public bool IsTooShort => Password.Length > 0 && !PasswordStrength.MeetsMasterPasswordMinimum(Password);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _main.FileDialogs.SaveFileAsync("保管庫の保存先", Path.GetFileName(VaultPath), "PwVault 保管庫", "pwv");
        if (path is not null) VaultPath = path;
    }

    [RelayCommand]
    private async Task OpenExistingAsync()
    {
        var path = await _main.FileDialogs.OpenFileAsync("既存の保管庫を開く", "PwVault 保管庫", "*.pwv");
        if (path is not null) _main.ShowUnlock(path);
    }

    [RelayCommand]
    private async Task SaveEmergencyKitAsync()
    {
        var path = await _main.FileDialogs.SaveFileAsync("緊急キットを保存", "PwVault-緊急キット.txt", "テキスト", "txt");
        if (path is null) return;
        await File.WriteAllTextAsync(path, $"""
            PwVault 緊急キット
            ==================
            作成日: {DateTime.Now:yyyy-MM-dd}

            保管庫ファイルの場所:
              {VaultPath}

            マスターパスワード（手書きで記入してください）:
              ________________________________________

            ・このキットは印刷して手書きで記入し、金庫など安全な場所に保管してください。
            ・マスターパスワードはどこにも保存されません。忘れると保管庫は復旧できません。
            ・保管庫ファイルと .bak ファイルは定期的に別の場所へバックアップしてください。
            """);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        Error = null;
        if (string.IsNullOrWhiteSpace(VaultPath)) { Error = "保存先を選んでください。"; return; }
        if (File.Exists(VaultPath)) { Error = "同じ名前のファイルが既にあります。別の名前にするか「既存の保管庫を開く」を使ってください。"; return; }
        if (!PasswordStrength.MeetsMasterPasswordMinimum(Password)) { Error = $"マスターパスワードは {PasswordStrength.MasterPasswordMinLength} 文字以上にしてください。"; return; }
        if (Password != ConfirmPassword) { Error = "確認用のパスワードが一致しません。"; return; }
        if (!Acknowledged) { Error = "緊急キットの注意事項を確認してチェックを入れてください。"; return; }

        if (Strength.IsWeak && !await _main.ConfirmAsync("弱いマスターパスワード",
                "このマスターパスワードは推測されやすい可能性があります。保管庫ファイルが盗まれた場合、総当たりで破られる危険が高まります。\nこのまま作成しますか？",
                "このまま作成", isDanger: true))
            return;

        IsBusy = true;
        try
        {
            var path = VaultPath;
            var password = Password;
            var generations = _main.Settings.BackupGenerations;
            var vault = await Task.Run(() =>
            {
                // この端末でアンロックが 0.5〜1 秒になるよう KDF のコストを計測して決める（SR-01）
                var kdf = KdfCalibrator.Calibrate();
                return Vault.Create(path, password, kdf, generations);
            });
            Password = ConfirmPassword = "";
            _main.OnUnlocked(vault);
            // はじめての保管庫なら、使い方の案内を出す（最初のページで「スキップ」もできる）
            if (!_main.Settings.TutorialSeen) _main.StartTutorial();
        }
        catch (VaultException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = "保管庫を作成できませんでした。保存先に書き込めるか確認してください。";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

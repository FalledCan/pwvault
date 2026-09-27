using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core.Interop;

namespace PwVault.App.ViewModels;

/// <summary>S8 インポート／エクスポート（FR-11, FR-12, SR-12）。</summary>
public partial class ImportExportViewModel : ViewModelBase
{
    private readonly VaultViewModel _owner;

    public ImportExportViewModel(VaultViewModel owner) => _owner = owner;

    [ObservableProperty] public partial string? ImportStatus { get; set; }
    [ObservableProperty] public partial string? BackupStatus { get; set; }
    [ObservableProperty] public partial string? CsvStatus { get; set; }
    [ObservableProperty] public partial string CsvPassword { get; set; } = "";
    [ObservableProperty] public partial bool CsvAcknowledged { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    [RelayCommand]
    private async Task ImportAsync()
    {
        ImportStatus = null;
        var path = await _owner.Main.FileDialogs.OpenFileAsync("CSV を取り込む", "CSV", "*.csv");
        if (path is null) return;

        ImportResult result;
        try
        {
            result = CsvImporter.Import(await File.ReadAllTextAsync(path), DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ImportStatus = "ファイルを読み込めませんでした。";
            return;
        }

        if (result.Format == CsvFormat.Unknown)
        {
            ImportStatus = "対応していない CSV 形式です（ブラウザ・Bitwarden・KeePassXC・PwVault の形式に対応）。";
            return;
        }

        // 同じタイトル・ユーザーID・パスワード・URL のエントリが既にあれば取り込まない
        var existing = _owner.Vault.GetEntries()
            .Select(e => (e.Data.Title, e.Data.Username, e.Data.Password, e.Data.Url)).ToHashSet();
        var toImport = result.Entries.Where(e => !existing.Contains((e.Title, e.Username, e.Password, e.Url))).ToList();
        var duplicates = result.Entries.Count - toImport.Count;

        var message = $"形式: {CsvImporter.FormatName(result.Format)}\n取り込む件数: {toImport.Count} 件" +
                      (duplicates > 0 ? $"\n既にある同じエントリ: {duplicates} 件（取り込みません）" : "") +
                      (result.SkippedRows > 0 ? $"\n読み飛ばした行: {result.SkippedRows} 件（ログイン以外・空行など）" : "");
        if (toImport.Count == 0)
        {
            ImportStatus = message;
            return;
        }
        if (!await _owner.Main.ConfirmAsync("CSV の取り込み", message, "取り込む"))
            return;

        foreach (var entry in toImport)
            _owner.Vault.AddEntry(entry);
        ImportStatus = _owner.Persist()
            ? $"{toImport.Count} 件を取り込みました。元の CSV ファイルは平文なので、不要になったら削除してください。"
            : _owner.Status;
    }

    /// <summary>暗号化バックアップ。保管庫ファイルをそのまま複製する（同じマスターパスワードで開ける）。</summary>
    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        BackupStatus = null;
        var name = $"{Path.GetFileNameWithoutExtension(_owner.Vault.FilePath)}-backup-{DateTime.Now:yyyyMMdd}.pwv";
        var dest = await _owner.Main.FileDialogs.SaveFileAsync("暗号化バックアップの保存先", name, "PwVault 保管庫", "pwv");
        if (dest is null) return;
        if (Path.GetFullPath(dest).Equals(_owner.Vault.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            BackupStatus = "保管庫そのものとは別の場所を選んでください。";
            return;
        }

        if (!_owner.Persist()) { BackupStatus = _owner.Status; return; }
        try
        {
            File.Copy(_owner.Vault.FilePath, dest, overwrite: true);
            BackupStatus = "暗号化バックアップを保存しました。現在のマスターパスワードで開けます。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            BackupStatus = "バックアップを保存できませんでした。";
        }
    }

    /// <summary>平文 CSV。警告に同意し、マスターパスワードで再認証したときだけ許可する（SR-12）。</summary>
    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        CsvStatus = null;
        if (!CsvAcknowledged) { CsvStatus = "警告の内容を確認してチェックを入れてください。"; return; }
        if (CsvPassword.Length == 0) { CsvStatus = "マスターパスワードを入力してください。"; return; }

        IsBusy = true;
        var password = CsvPassword;
        bool ok;
        try { ok = await Task.Run(() => _owner.Vault.VerifyPassword(password)); }
        finally { IsBusy = false; }
        CsvPassword = "";
        if (!ok) { CsvStatus = "マスターパスワードが違います。"; return; }

        var dest = await _owner.Main.FileDialogs.SaveFileAsync("平文 CSV の保存先", "pwvault-export.csv", "CSV", "csv");
        if (dest is null) return;

        try
        {
            // Excel で文字化けしないよう BOM 付き UTF-8
            await File.WriteAllTextAsync(dest, CsvExporter.Export(_owner.Vault.GetEntries()), new UTF8Encoding(true));
            CsvAcknowledged = false;
            CsvStatus = "平文 CSV を書き出しました。使い終わったら必ず削除してください（ごみ箱も空にしてください）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CsvStatus = "CSV を保存できませんでした。";
        }
    }

    [RelayCommand]
    private void Back() => _owner.CloseSubPage();
}

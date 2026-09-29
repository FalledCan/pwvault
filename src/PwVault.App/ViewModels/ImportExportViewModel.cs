using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core;
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

    // ------------------------------------------------------------------ Google Authenticator から移す

    /// <summary>読み取ったアカウント（取り込む前の確認用）。</summary>
    public ObservableCollection<OtpImportItem> OtpItems { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtpItems))]
    public partial int OtpItemCount { get; set; }

    public bool HasOtpItems => OtpItemCount > 0;

    [ObservableProperty]
    public partial string? OtpStatus { get; set; }

    private IReadOnlyList<OtpTarget>? _otpTargets;
    private int? _batchId;
    private readonly HashSet<int> _batchesRead = [];
    private int _batchSize;
    private int _unsupported;

    [RelayCommand]
    private void ScanMigration()
    {
        OtpStatus = null;
        _owner.OpenQrScan(
            "Google Authenticator から移す",
            "スマホの Google Authenticator で「≡（または …）→ アカウントを移行 → アカウントをエクスポート」を選び、移すアカウントを選んで「次へ」を押すと QR コードが出ます。" +
            "それをカメラに写してください（QR コードが何枚かに分かれていたら、1 枚ずつ写します）。スマホのアカウントは消えません。",
            AcceptMigration);
    }

    /// <summary>読み取った QR コード 1 枚を受け取る。全部読めたら（または普通の 1 件の QR なら）閉じる。</summary>
    internal QrAccept AcceptMigration(string text)
    {
        if (!Core.Otp.GoogleAuthMigration.IsMigrationUri(text))
        {
            // サイトが出す普通の QR（1 件）も受け付ける
            if (!Core.Otp.TotpKey.TryParse(text, out var single, out var error))
                return new QrAccept(false, "Google Authenticator の移行用 QR コードではありません。" + (text.StartsWith("otpauth", StringComparison.OrdinalIgnoreCase) ? error : ""));
            AddOtpKeys([single!]);
            return new QrAccept(true);
        }

        if (!Core.Otp.GoogleAuthMigration.TryParse(text, out var batch, out var migrationError))
            return new QrAccept(false, migrationError);

        if (_batchId != batch!.BatchId)
        {
            // 別の書き出し（やり直し）の QR。枚数の数え直し
            _batchId = batch.BatchId;
            _batchesRead.Clear();
        }
        _batchSize = batch.BatchSize;
        if (!_batchesRead.Add(batch.BatchIndex))
            return new QrAccept(false, $"{batch.BatchIndex + 1} 枚目は読み取り済みです。スマホで「次へ」を押して、次の QR コードを写してください。");

        AddOtpKeys(batch.Keys);
        _unsupported += batch.Unsupported;
        if (_batchesRead.Count >= _batchSize)
            return new QrAccept(true);
        return new QrAccept(false, $"{_batchesRead.Count} / {_batchSize} 枚を読み取りました。スマホで「次へ」を押して、次の QR コードを写してください。");
    }

    private void AddOtpKeys(IEnumerable<Core.Otp.TotpKey> keys)
    {
        var entries = _owner.Vault.GetEntries().Where(e => !e.Data.IsTrashed).ToList();
        _otpTargets ??=
        [
            new OtpTarget(null, "＋ 新しいエントリを作る"),
            .. entries.OrderBy(e => e.Data.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new OtpTarget(e.Id, e.Data.Username.Length > 0 ? $"{e.Data.Title}（{e.Data.Username}）" : e.Data.Title)),
        ];

        foreach (var key in keys)
        {
            if (OtpItems.Any(i => i.Key.SameSecret(key))) continue; // 同じ QR を 2 回読んだ
            var registered = entries.FirstOrDefault(e => Core.Otp.TotpKey.FromStored(e.Data.Totp)?.SameSecret(key) == true);
            var suggested = registered ?? Core.Otp.TotpMatcher.Suggest(entries, key);
            var target = _otpTargets.FirstOrDefault(t => t.Id == suggested?.Id) ?? _otpTargets[0];
            OtpItems.Add(new OtpImportItem(key, _otpTargets, target,
                registered is null ? null : $"「{registered.Data.Title}」に登録済みです（取り込まなくても使えます）"));
        }
        OtpItemCount = OtpItems.Count;
        var missing = _batchSize > 0 && _batchesRead.Count < _batchSize ? $"\nQR コードは {_batchSize} 枚のうち {_batchesRead.Count} 枚しか読み取っていません。残りは「QR コードを読み取る…」で続けて読み取れます。" : "";
        OtpStatus = $"{OtpItems.Count} 件のアカウントを読み取りました。取り込み先を確かめて「取り込む」を押してください。" +
                    (_unsupported > 0 ? $"\n{_unsupported} 件は対応していない方式（回数で変わるもの）のため取り込めません。" : "") + missing;
    }

    [RelayCommand]
    private async Task ImportOtpAsync()
    {
        var selected = OtpItems.Where(i => i.Include).ToList();
        if (selected.Count == 0)
        {
            OtpStatus = "取り込むアカウントにチェックを入れてください。";
            return;
        }
        var duplicate = selected.Where(i => i.SelectedTarget.Id is not null).GroupBy(i => i.SelectedTarget.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            OtpStatus = $"「{duplicate.First().SelectedTarget.Label}」に 2 つ以上のアカウントを取り込もうとしています。1 つのエントリには 1 つだけです。";
            return;
        }

        var vault = _owner.Vault;
        var created = selected.Count(i => i.SelectedTarget.Id is null);
        var replaced = selected.Count(i => i.SelectedTarget.Id is { } id && vault.GetEntry(id)?.Data is { HasTotp: true } d &&
                                           Core.Otp.TotpKey.FromStored(d.Totp)?.SameSecret(i.Key) != true);
        var message = $"{selected.Count} 件のワンタイムパスワードを取り込みます。\n新しいエントリを作る: {created} 件\n既にあるエントリに追加: {selected.Count - created} 件" +
                      (replaced > 0 ? $"\nそのうち {replaced} 件は、今のワンタイムパスワードのキーを置き換えます。" : "");
        if (!await _owner.Main.ConfirmAsync("ワンタイムパスワードの取り込み", message, "取り込む"))
            return;

        var now = _owner.Main.Clock.GetUtcNow();
        foreach (var item in selected)
        {
            var key = item.Key;
            if (item.SelectedTarget.Id is { } id && vault.GetEntry(id) is { } entry)
            {
                var data = entry.Data.Clone();
                data.Totp = key.WithNames(key.Issuer.Length > 0 ? key.Issuer : data.Title, key.Account.Length > 0 ? key.Account : data.Username).ToUri();
                vault.UpdateEntry(id, data);
            }
            else
            {
                var title = key.Issuer.Length > 0 ? key.Issuer : key.Account.Length > 0 ? key.Account : "ワンタイムパスワード";
                vault.AddEntry(new EntryData { Title = title, Username = key.Account, Totp = key.ToUri(), CreatedAt = now });
            }
        }

        if (!_owner.Persist())
        {
            OtpStatus = _owner.Status;
            return;
        }
        ClearOtp();
        _owner.Refresh();
        OtpStatus = $"{selected.Count} 件のワンタイムパスワードを取り込みました。スマホの Google Authenticator のアカウントは、そのまま残して使えます。" +
                    (created > 0 ? "\n新しく作ったエントリには URL が入っていません。ブラウザで使うには、編集で URL を入れてください。" : "");
    }

    [RelayCommand]
    private void CancelOtp()
    {
        ClearOtp();
        OtpStatus = null;
    }

    private void ClearOtp()
    {
        OtpItems.Clear();
        OtpItemCount = 0;
        _otpTargets = null;
        _batchId = null;
        _batchesRead.Clear();
        _batchSize = 0;
        _unsupported = 0;
    }
}

/// <summary>「Google Authenticator から移す」の取り込み先の選択肢（Id が null なら新しいエントリを作る）。</summary>
public sealed record OtpTarget(Guid? Id, string Label);

/// <summary>読み取ったアカウント 1 件と、その取り込み先。</summary>
public sealed partial class OtpImportItem(Core.Otp.TotpKey key, IReadOnlyList<OtpTarget> targets, OtpTarget selected, string? note) : ObservableObject
{
    public Core.Otp.TotpKey Key { get; } = key;

    public string Label => Key.Issuer.Length > 0 && Key.Account.Length > 0 ? $"{Key.Issuer}（{Key.Account}）"
        : Key.Issuer.Length > 0 ? Key.Issuer : Key.Account.Length > 0 ? Key.Account : "（名前なし）";

    public IReadOnlyList<OtpTarget> Targets { get; } = targets;

    [ObservableProperty]
    public partial OtpTarget SelectedTarget { get; set; } = selected;

    [ObservableProperty]
    public partial bool Include { get; set; } = note is null;

    /// <summary>既に登録済みなど、知らせておくこと。</summary>
    public string? Note { get; } = note;
    public bool HasNote => Note is not null;
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PwVault.Core;
using PwVault.Core.Tools;

namespace PwVault.App.ViewModels;

/// <summary>S5 エントリ編集（FR-04, FR-05）。</summary>
public partial class EntryEditorViewModel : ViewModelBase
{
    private readonly VaultViewModel _owner;
    private readonly Guid? _id;
    private readonly EntryData _original;

    public EntryEditorViewModel(VaultViewModel owner, VaultEntry? entry)
    {
        _owner = owner;
        _id = entry?.Id;
        _original = entry?.Data.Clone() ?? new EntryData();
        EntryTitle = _original.Title;
        Username = _original.Username;
        Password = _original.Password;
        Url = _original.Url;
        Notes = _original.Notes;
        TagsText = string.Join(", ", _original.Tags);
        Favorite = _original.Favorite;
        AutoTypeAppsText = string.Join(", ", _original.AutoTypeApps);
        AutoTypePasswordOnly = _original.AutoType == AutoTypeMode.PasswordOnly;
        SelectedOnOpen = OnOpenOptions.First(o => o.Value == _original.AutoTypeOnOpen);
        AutoTypeIgnoreTitlesText = string.Join(Environment.NewLine, _original.AutoTypeIgnoreTitles);
        TotpText = _original.Totp;
        AutoTypeTotp = _original.AutoTypeTotp;
    }

    // ------------------------------------------------------------------ ワンタイムパスワード

    /// <summary>ワンタイムパスワードのキー（サイトに出るキーの文字列か otpauth://）。空なら使わない。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotpPreview), nameof(HasTotpText))]
    public partial string TotpText { get; set; } = "";

    public bool HasTotpText => TotpText.Trim().Length > 0;

    [ObservableProperty]
    public partial bool RevealTotp { get; set; }

    /// <summary>自動入力で、パスワードの後に Tab → ワンタイムパスワードも打つ。</summary>
    [ObservableProperty]
    public partial bool AutoTypeTotp { get; set; }

    /// <summary>入力中のキーで作った今のコード（スマホの番号と同じか確かめてもらう）。読めなければ理由。</summary>
    public string TotpPreview
    {
        get
        {
            if (!HasTotpText) return "";
            if (!Core.Otp.TotpKey.TryParse(TotpText, out var key, out var error)) return "⚠ " + error;
            var now = _owner.Main.Clock.GetUtcNow();
            return $"今のコード: {VaultViewModel.FormatCode(key!.Generate(now))}（あと {key.SecondsRemaining(now)} 秒）… スマホの認証アプリと同じ番号なら正しく登録できています。";
        }
    }

    /// <summary>コードの表示を新しくする（表示中は 1 秒ごと）。</summary>
    public void RefreshTotpPreview() => OnPropertyChanged(nameof(TotpPreview));

    [RelayCommand]
    private void ScanTotpQr() => _owner.OpenQrScan(
        "ワンタイムパスワードの QR コードを読み取る",
        "サイトの 2 段階認証の設定画面に出る QR コードを、カメラに写すか、画像で読み込んでください。",
        text =>
        {
            Core.Otp.TotpKey? key = null;
            if (Core.Otp.GoogleAuthMigration.IsMigrationUri(text))
            {
                // 移行用の QR でも、1 件だけならそのまま使う
                if (Core.Otp.GoogleAuthMigration.TryParse(text, out var batch, out _) && batch!.Keys.Count == 1)
                    key = batch.Keys[0];
                else
                    return new QrAccept(false, "これは Google Authenticator の移行用の QR コード（複数のアカウント）です。「取込/書出」の「Google Authenticator から移す」で取り込んでください。");
            }
            else if (!Core.Otp.TotpKey.TryParse(text, out key, out var error))
            {
                return new QrAccept(false, "この QR コードは使えません: " + error);
            }
            TotpText = key!.ToUri();
            return new QrAccept(true);
        });

    /// <summary>アプリが開いたときに反応しない画面の名前（1 行に 1 つ。題名にこの文字を含む画面）。</summary>
    [ObservableProperty]
    public partial string AutoTypeIgnoreTitlesText { get; set; }

    public sealed record OnOpenOption(AutoTypeOnOpen Value, string Label);

    /// <summary>紐付けたアプリが前に出たときの動きの選択肢。</summary>
    public IReadOnlyList<OnOpenOption> OnOpenOptions { get; } =
    [
        new(AutoTypeOnOpen.ShowPicker, "候補を出す（Enter で入力）"),
        new(AutoTypeOnOpen.TypeAutomatically, "数秒後に自動で入力する"),
        new(AutoTypeOnOpen.None, "何もしない（ショートカットキーのときだけ）"),
    ];

    [ObservableProperty]
    public partial OnOpenOption SelectedOnOpen { get; set; }

    /// <summary>自動入力の設定欄を出すか（Windows のみ）。</summary>
    public bool AutoTypeSupported => _owner.Main.AutoTypeSupported;

    /// <summary>自動入力してよいアプリ（カンマ区切りの実行ファイル名）。</summary>
    [ObservableProperty]
    public partial string AutoTypeAppsText { get; set; }

    /// <summary>自動入力でパスワードだけを打つ（ID は打たない）。</summary>
    [ObservableProperty]
    public partial bool AutoTypePasswordOnly { get; set; }

    public bool IsNew => _id is null;
    public string Heading => IsNew ? "新しいエントリ" : "エントリを編集";

    [ObservableProperty]
    public partial string EntryTitle { get; set; }

    [ObservableProperty]
    public partial string Username { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthText), nameof(StrengthValue))]
    public partial string Password { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; }

    [ObservableProperty]
    public partial string Notes { get; set; }

    [ObservableProperty]
    public partial string TagsText { get; set; }

    [ObservableProperty]
    public partial bool Favorite { get; set; }

    [ObservableProperty]
    public partial bool RevealPassword { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public int StrengthValue => Password.Length == 0 ? 0 : (int)PasswordStrength.Evaluate(Password).Level + 1;

    public string StrengthText => Password.Length == 0 ? "" : "強度: " + PasswordStrength.Evaluate(Password).Label;

    [RelayCommand]
    private void ToggleReveal() => RevealPassword = !RevealPassword;

    [RelayCommand]
    private void Generate() => _owner.OpenGeneratorFor(pw =>
    {
        Password = pw;
        RevealPassword = true;
    });

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EntryTitle) && string.IsNullOrWhiteSpace(Url))
        {
            Error = "タイトルか URL のどちらかは入力してください。";
            return;
        }

        var totp = "";
        if (HasTotpText)
        {
            if (!Core.Otp.TotpKey.TryParse(TotpText, out _, out var totpError))
            {
                Error = "ワンタイムパスワードのキー: " + totpError;
                return;
            }
            totp = Core.Otp.TotpKey.Normalize(TotpText, EntryTitle, Username);
        }

        var data = _original.Clone();
        data.Totp = totp;
        data.AutoTypeTotp = AutoTypeTotp;
        data.Title = EntryTitle.Trim();
        data.Username = Username.Trim();
        data.Password = Password;
        data.Url = Url.Trim();
        data.Notes = Notes;
        data.Tags = TagsText.Split([',', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        data.Favorite = Favorite;
        data.AutoTypeApps = AutoTypeMatcher.Parse(AutoTypeAppsText);
        data.AutoType = AutoTypePasswordOnly ? AutoTypeMode.PasswordOnly : AutoTypeMode.UsernameTabPassword;
        data.AutoTypeOnOpen = SelectedOnOpen.Value;
        data.AutoTypeIgnoreTitles = AutoTypeIgnoreTitlesText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        _owner.CommitEditor(_id, data);
    }

    [RelayCommand]
    private void Cancel() => _owner.CancelEditor();
}

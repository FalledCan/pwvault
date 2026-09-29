using System.Text.Json.Serialization;

namespace PwVault.Core;

/// <summary>エントリ本体（暗号化前、要件 6.2）。すべて暗号文の中に入る。</summary>
public sealed class EntryData
{
    public string Title { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public List<PasswordHistoryItem> History { get; set; } = [];
    public bool Favorite { get; set; }

    /// <summary>ゴミ箱に入れた日時。null ならゴミ箱外。完全削除（墓標）とは別物。</summary>
    public DateTimeOffset? TrashedAt { get; set; }

    /// <summary>
    /// 自動タイプで入力してよいアプリ（実行ファイル名。例: ffxivboot.exe）。
    /// このアプリが前面のときは候補の先頭に出し、確認なしで入力する。
    /// </summary>
    public List<string> AutoTypeApps { get; set; } = [];

    /// <summary>自動タイプで打ち込む内容。</summary>
    public AutoTypeMode AutoType { get; set; } = AutoTypeMode.UsernameTabPassword;

    /// <summary>紐付けたアプリの画面が前に出たときの動き（アンロック中だけ）。</summary>
    public AutoTypeOnOpen AutoTypeOnOpen { get; set; } = AutoTypeOnOpen.ShowPicker;

    /// <summary>
    /// アプリが開いたときの自動入力で反応しない画面の名前（題名にこの文字を含む画面。更新の画面など）。
    /// 候補・通知の「この画面では出さない」で追加される。
    /// </summary>
    public List<string> AutoTypeIgnoreTitles { get; set; } = [];

    /// <summary>
    /// ワンタイムパスワード（TOTP）の元。otpauth://totp/... の形（<see cref="Otp.TotpKey.ToUri"/>）。空なら使わない。
    /// </summary>
    public string Totp { get; set; } = "";

    /// <summary>自動タイプで、パスワードの後に Tab → ワンタイムパスワードも打つ。</summary>
    public bool AutoTypeTotp { get; set; }

    [JsonIgnore]
    public bool HasTotp => Totp.Length > 0;

    [JsonIgnore]
    public bool IsTrashed => TrashedAt is not null;

    public EntryData Clone() => new()
    {
        Title = Title,
        Username = Username,
        Password = Password,
        Url = Url,
        Notes = Notes,
        Tags = [.. Tags],
        CreatedAt = CreatedAt,
        History = History.Select(h => h with { }).ToList(),
        Favorite = Favorite,
        TrashedAt = TrashedAt,
        AutoTypeApps = [.. AutoTypeApps],
        AutoType = AutoType,
        AutoTypeOnOpen = AutoTypeOnOpen,
        AutoTypeIgnoreTitles = [.. AutoTypeIgnoreTitles],
        Totp = Totp,
        AutoTypeTotp = AutoTypeTotp,
    };
}

/// <summary>紐付けたアプリの画面が前に出たときの動き。</summary>
public enum AutoTypeOnOpen
{
    /// <summary>何もしない（ショートカットキーを押したときだけ入力する）。</summary>
    None,
    /// <summary>選択窓を出して、このエントリを選んだ状態にする（Enter で入力）。</summary>
    ShowPicker,
    /// <summary>数秒待ってから自動で打ち込む（待っている間は「やめる」を出す）。</summary>
    TypeAutomatically,
}

/// <summary>自動タイプで打ち込む内容（Enter は押さない）。</summary>
public enum AutoTypeMode
{
    /// <summary>ユーザー ID → Tab → パスワード。</summary>
    UsernameTabPassword,
    /// <summary>パスワードだけ（ID が記憶されている画面など）。</summary>
    PasswordOnly,
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EntryData))]
internal sealed partial class EntryJsonContext : JsonSerializerContext;

/// <summary>パスワード変更履歴の 1 件（FR-15）。</summary>
public sealed record PasswordHistoryItem(string Password, DateTimeOffset ChangedAt);

/// <summary>UI に渡すエントリ。Data は複製なので、変更は <see cref="Vault.UpdateEntry"/> で反映する。</summary>
public sealed record VaultEntry(Guid Id, long Revision, DateTimeOffset UpdatedAt, EntryData Data);

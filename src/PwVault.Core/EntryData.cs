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
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(EntryData))]
internal sealed partial class EntryJsonContext : JsonSerializerContext;

/// <summary>パスワード変更履歴の 1 件（FR-15）。</summary>
public sealed record PasswordHistoryItem(string Password, DateTimeOffset ChangedAt);

/// <summary>UI に渡すエントリ。Data は複製なので、変更は <see cref="Vault.UpdateEntry"/> で反映する。</summary>
public sealed record VaultEntry(Guid Id, long Revision, DateTimeOffset UpdatedAt, EntryData Data);

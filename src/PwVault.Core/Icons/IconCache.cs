using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using PwVault.Core.Crypto;
using PwVault.Core.Storage;

namespace PwVault.Core.Icons;

/// <summary>アイコンの入手元。ブラウザ拡張から受け取ったものを優先する。</summary>
public enum IconSource
{
    /// <summary>PwVault が各サイトから直接取得した。</summary>
    Site,
    /// <summary>ブラウザ拡張が、開いているタブのアイコンを渡してきた（通信なし）。</summary>
    Browser,
}

public sealed record IconEntry(IconSource Source, DateTimeOffset UpdatedAt, byte[]? Data)
{
    /// <summary>取得を試みたがアイコンが無かった（取り直しを控えるための記録）。</summary>
    public bool IsMissing => Data is null;
}

/// <summary>
/// サイトのアイコンのキャッシュ。ホスト名をキーに、保管庫鍵で暗号化して保管庫の隣（*.pwv.icons）に保存する。
/// 平文で保存すると「どのサイトにアカウントがあるか」がファイルから読めてしまうため、ホスト名ごと暗号化する。
/// </summary>
public sealed class IconCache
{
    public const string Purpose = "icons";
    public const int MaxIconBytes = 64 * 1024;
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryMissingAfter = TimeSpan.FromDays(7);

    private readonly Dictionary<string, IconEntry> _items;

    private IconCache(Dictionary<string, IconEntry> items) => _items = items;

    public static IconCache Empty() => new([]);

    public int Count => _items.Count;

    public static string PathFor(string vaultPath) => vaultPath + ".icons";

    public IconEntry? Get(string host) => _items.GetValueOrDefault(host);

    /// <summary>ブラウザから受け取ったアイコンを保存する（常に上書き）。</summary>
    public void SetFromBrowser(string host, byte[] data, DateTimeOffset now) =>
        _items[host] = new IconEntry(IconSource.Browser, now, Checked(data));

    /// <summary>サイトから取得した結果を保存する。ブラウザ由来のアイコンがあれば上書きしない。null は「無かった」。</summary>
    public bool SetFromSite(string host, byte[]? data, DateTimeOffset now)
    {
        if (_items.TryGetValue(host, out var current) && current is { Source: IconSource.Browser, IsMissing: false })
            return false;
        _items[host] = new IconEntry(IconSource.Site, now, data is null ? null : Checked(data));
        return true;
    }

    /// <summary>サイトから取りに行くべきか（未取得、古い、または「無かった」記録から一定期間たった）。</summary>
    public bool NeedsSiteFetch(string host, DateTimeOffset now) =>
        _items.GetValueOrDefault(host) switch
        {
            null => true,
            { Source: IconSource.Browser, IsMissing: false } => false,
            { IsMissing: true } e => now - e.UpdatedAt >= RetryMissingAfter,
            var e => now - e.UpdatedAt >= RefreshAfter,
        };

    /// <summary>もう使っていないホストを消す（削除したエントリのアイコンを残さない）。</summary>
    public int RemoveAllExcept(IReadOnlySet<string> hosts)
    {
        var stale = _items.Keys.Where(h => !hosts.Contains(h)).ToList();
        foreach (var host in stale)
            _items.Remove(host);
        return stale.Count;
    }

    // ------------------------------------------------------------------ 保存・読み込み

    public void Save(Vault vault, string path)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(
            new IconCacheDto { Items = _items.Select(kv => new IconItemDto(kv.Key, kv.Value.Source, kv.Value.UpdatedAt, kv.Value.IsMissing, kv.Value.Data ?? [])).ToList() },
            IconJsonContext.Default.IconCacheDto);
        try
        {
            var box = vault.SealAuxiliary(Purpose, plain);
            var file = JsonSerializer.SerializeToUtf8Bytes(
                new IconFileDto { Magic = "PWVI1", VaultId = vault.VaultId.ToString("D"), Nonce = box.Nonce, Ciphertext = box.Ciphertext },
                IconJsonContext.Default.IconFileDto);
            AtomicFileStore.Write(path, file, backupGenerations: 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>
    /// 読み込む。ファイルが無い・壊れている・別の保管庫のものなら空のキャッシュを返す
    /// （アイコンは取り直せばよいので、ここでの失敗は致命的にしない）。
    /// </summary>
    public static IconCache Load(Vault vault, string path)
    {
        try
        {
            if (!File.Exists(path)) return Empty();
            var file = JsonSerializer.Deserialize(File.ReadAllBytes(path), IconJsonContext.Default.IconFileDto);
            if (file is not { Magic: "PWVI1", Nonce: not null, Ciphertext: not null } || file.VaultId != vault.VaultId.ToString("D"))
                return Empty();

            using var plain = vault.OpenAuxiliary(Purpose, new SealedBox(file.Nonce, file.Ciphertext));
            if (plain is null) return Empty();

            var dto = JsonSerializer.Deserialize(plain.Span, IconJsonContext.Default.IconCacheDto);
            var items = new Dictionary<string, IconEntry>();
            foreach (var i in dto?.Items ?? [])
            {
                var missing = i.Missing || i.Data is not { Length: > 0 };
                if (i.Host is { Length: > 0 } && (missing || i.Data!.Length <= MaxIconBytes))
                    items[i.Host] = new IconEntry(i.Source, i.UpdatedAt, missing ? null : i.Data);
            }
            return new IconCache(items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty();
        }
    }

    private static byte[] Checked(byte[] data) =>
        data.Length is > 0 and <= MaxIconBytes ? data : throw new ArgumentException("アイコンの大きさが範囲外です。", nameof(data));
}

internal sealed class IconFileDto
{
    public string? Magic { get; set; }
    public string? VaultId { get; set; }
    public byte[]? Nonce { get; set; }
    public byte[]? Ciphertext { get; set; }
}

internal sealed class IconCacheDto
{
    public List<IconItemDto>? Items { get; set; }
}

/// <summary>Missing は「取得したが無かった」の記録（null の配列に頼らず明示する）。</summary>
internal sealed record IconItemDto(string Host, IconSource Source, DateTimeOffset UpdatedAt, bool Missing, byte[]? Data);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IconFileDto))]
[JsonSerializable(typeof(IconCacheDto))]
internal sealed partial class IconJsonContext : JsonSerializerContext;

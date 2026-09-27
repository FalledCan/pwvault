using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PwVault.Core.Crypto;

namespace PwVault.Core.Format;

/// <summary>保管庫ファイルの中身（ヘッダ＋暗号化済みエントリ）。</summary>
public sealed record VaultDocument(VaultHeader Header, IReadOnlyList<EncryptedEntry> Entries);

/// <summary>
/// 保管庫ファイルと <see cref="VaultDocument"/> の相互変換。シリアライズ形式は JSON（UTF-8）。
/// 末尾の checksum（SHA-256）は鍵を使わない単なる整合性チェックで、偶発的な破損を
/// 「パスワード誤り」と区別するために使う（NFR-06）。改ざん検知は AEAD が担う。
/// </summary>
public static class VaultFileCodec
{
    private const string ChecksumPrefix = "sha256:";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static byte[] Serialize(VaultDocument document)
    {
        var dto = ToDto(document);
        dto.Checksum = null;
        dto.Checksum = ChecksumPrefix + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dto, Options)));
        return JsonSerializer.SerializeToUtf8Bytes(dto, Options);
    }

    public static VaultDocument Deserialize(ReadOnlySpan<byte> bytes)
    {
        VaultFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<VaultFileDto>(bytes, Options);
        }
        catch (JsonException ex)
        {
            throw Invalid("保管庫ファイルの形式が正しくありません。", ex);
        }

        if (dto is null || dto.Magic != VaultHeader.Magic)
            throw Invalid("保管庫ファイルではないか、先頭部分が壊れています。");

        if (dto.FormatVersion > VaultHeader.CurrentFormatVersion)
            throw new VaultException(VaultErrorKind.UnsupportedVersion,
                $"この保管庫はより新しいバージョンのアプリで作成されています（フォーマット版 {dto.FormatVersion}）。アプリを更新してください。");

        VerifyChecksum(dto);
        Migrate(dto);
        return FromDto(dto);
    }

    private static void VerifyChecksum(VaultFileDto dto)
    {
        var stored = dto.Checksum;
        dto.Checksum = null;
        var actual = ChecksumPrefix + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dto, Options)));
        dto.Checksum = stored;

        if (stored is null || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(actual)))
            throw new VaultException(VaultErrorKind.Corrupted, "保管庫ファイルが破損しています（チェックサム不一致）。");
    }

    /// <summary>
    /// 古いフォーマット版を現在の版へ変換する（NFR-08）。現時点では版 1 のみなので何もしない。
    /// 版を上げるときは、ここに「版 n → n+1」の変換を順に追加する。
    /// </summary>
    private static void Migrate(VaultFileDto dto)
    {
        if (dto.FormatVersion < 1)
            throw Invalid($"未知のフォーマット版です（{dto.FormatVersion}）。");
    }

    private static VaultFileDto ToDto(VaultDocument doc) => new()
    {
        Magic = VaultHeader.Magic,
        FormatVersion = doc.Header.FormatVersion,
        VaultId = doc.Header.VaultId.ToString("D"),
        Kdf = new KdfDto
        {
            Alg = KdfParameters.Algorithm,
            MemKib = doc.Header.Kdf.MemoryKiB,
            Iterations = doc.Header.Kdf.Iterations,
            Parallelism = doc.Header.Kdf.Parallelism,
            Salt = doc.Header.Kdf.Salt,
        },
        Cipher = VaultHeader.Cipher,
        WrappedVaultKey = new BoxDto { Nonce = doc.Header.WrappedVaultKey.Nonce, Ciphertext = doc.Header.WrappedVaultKey.Ciphertext },
        Entries = doc.Entries.Select(e => new EntryDto
        {
            Id = e.Id.ToString("D"),
            Revision = e.Revision,
            UpdatedAt = e.UpdatedAt,
            Deleted = e.Deleted,
            Nonce = e.Box.Nonce,
            Ciphertext = e.Box.Ciphertext,
        }).ToList(),
    };

    private static VaultDocument FromDto(VaultFileDto dto)
    {
        if (dto.Kdf is not { Alg: KdfParameters.Algorithm, Salt: not null })
            throw Invalid("KDF の設定が読み取れません。");
        if (dto.Cipher != VaultHeader.Cipher)
            throw Invalid("未対応の暗号方式です。");
        if (!Guid.TryParse(dto.VaultId, out var vaultId))
            throw Invalid("保管庫 ID が読み取れません。");
        if (dto.WrappedVaultKey is not { Nonce: not null, Ciphertext: not null })
            throw Invalid("保管庫鍵が読み取れません。");

        var kdf = new KdfParameters(dto.Kdf.MemKib, dto.Kdf.Iterations, dto.Kdf.Parallelism, dto.Kdf.Salt);
        if (!kdf.IsValid)
            throw Invalid("KDF パラメータが許容範囲外です。");

        var header = new VaultHeader
        {
            FormatVersion = dto.FormatVersion,
            VaultId = vaultId,
            Kdf = kdf,
            WrappedVaultKey = new SealedBox(dto.WrappedVaultKey.Nonce, dto.WrappedVaultKey.Ciphertext),
        };

        var entries = new List<EncryptedEntry>();
        var seen = new HashSet<Guid>();
        foreach (var e in dto.Entries ?? [])
        {
            if (!Guid.TryParse(e.Id, out var id) || !seen.Add(id))
                throw Invalid("エントリ ID が不正、または重複しています。");
            if (e.Nonce is null || e.Ciphertext is null || e.Revision < 0 || !EncryptedEntry.IsValidTimestamp(e.UpdatedAt))
                throw Invalid("エントリの形式が正しくありません。");

            entries.Add(new EncryptedEntry
            {
                Id = id,
                Revision = e.Revision,
                UpdatedAt = e.UpdatedAt!,
                Deleted = e.Deleted,
                Box = new SealedBox(e.Nonce, e.Ciphertext),
            });
        }

        return new VaultDocument(header, entries);
    }

    private static VaultException Invalid(string message, Exception? inner = null) =>
        new(VaultErrorKind.InvalidFormat, message, inner);

    // ---- JSON DTO（プロパティ順がそのままファイル上の順になる） ----

    private sealed class VaultFileDto
    {
        public string? Magic { get; set; }
        public int FormatVersion { get; set; }
        public string? VaultId { get; set; }
        public KdfDto? Kdf { get; set; }
        public string? Cipher { get; set; }
        public BoxDto? WrappedVaultKey { get; set; }
        public List<EntryDto>? Entries { get; set; }
        public string? Checksum { get; set; }
    }

    private sealed class KdfDto
    {
        public string? Alg { get; set; }
        public int MemKib { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }
        public byte[]? Salt { get; set; }
    }

    private sealed class BoxDto
    {
        public byte[]? Nonce { get; set; }
        public byte[]? Ciphertext { get; set; }
    }

    private sealed class EntryDto
    {
        public string? Id { get; set; }
        public long Revision { get; set; }
        public string? UpdatedAt { get; set; }
        public bool Deleted { get; set; }
        public byte[]? Nonce { get; set; }
        public byte[]? Ciphertext { get; set; }
    }
}

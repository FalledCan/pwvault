using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using PwVault.Core.Crypto;
using PwVault.Core.Storage;

namespace PwVault.Core.QuickUnlock;

/// <summary>この端末に保存するクイックアンロックの登録内容。</summary>
public sealed record QuickUnlockRecord(Guid VaultId, byte[] Challenge, SealedBox WrappedVaultKey, SealedBox Expiry);

/// <summary>
/// Windows Hello などによるクイックアンロック（マスターパスワードの代わりに生体認証・PIN で開く）。
///
/// ・端末の認証機能（Windows Hello）が守る取り出せない鍵で、登録時に作った乱数（challenge）に署名してもらう
/// ・署名（RSA PKCS#1 v1.5 なので毎回同じ値になる）から HKDF で鍵を作り、その鍵で保管庫鍵を包んで端末に保存する
/// ・次回は同じ challenge に署名してもらえば同じ鍵ができ、保管庫鍵を取り出せる
/// ・有効期限は保管庫鍵で暗号化して保存するので、ファイルを書き換えても延ばせない。期限が切れたらマスターパスワードが必要
/// ・保管庫ファイルが盗まれても、この登録は端末側にしかないので影響しない
/// </summary>
public static class QuickUnlockService
{
    public const string Context = "windows-hello/v1";
    public const string ExpiryPurpose = "quick-unlock-expiry";
    public const int ChallengeSize = 32;
    private const int MinSignatureSize = 64;

    private static ReadOnlySpan<byte> Info => "pwvault/quick-unlock/v1"u8;

    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(ChallengeSize);

    /// <summary>署名から包み鍵を導出する。</summary>
    private static SecretBuffer DeriveWrappingKey(ReadOnlySpan<byte> signature, ReadOnlySpan<byte> challenge)
    {
        if (signature.Length < MinSignatureSize)
            throw new ArgumentException("署名が短すぎます。", nameof(signature));
        var key = SecretBuffer.Allocate(32);
        KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(signature, challenge, Info, key.Span);
        return key;
    }

    /// <summary>登録する（アンロック中の保管庫と、challenge への署名が必要）。</summary>
    public static QuickUnlockRecord Enroll(Vault vault, byte[] challenge, ReadOnlySpan<byte> signature, DateTimeOffset expiresAt)
    {
        using var key = DeriveWrappingKey(signature, challenge);
        return new QuickUnlockRecord(vault.VaultId, challenge, vault.SealVaultKeyWith(key.Span, Context), SealExpiry(vault, expiresAt));
    }

    /// <summary>マスターパスワードでアンロックしたときに期限を延ばす（Windows Hello の操作は不要）。</summary>
    public static QuickUnlockRecord RefreshExpiry(Vault vault, QuickUnlockRecord record, DateTimeOffset expiresAt) =>
        record.VaultId == vault.VaultId ? record with { Expiry = SealExpiry(vault, expiresAt) } : record;

    /// <summary>
    /// 署名を使ってアンロックする。登録が無効（マスターパスワード変更後・期限切れ・署名が違う）なら
    /// <see cref="VaultErrorKind.QuickUnlockUnavailable"/> の例外。
    /// </summary>
    public static Vault Unlock(string vaultPath, QuickUnlockRecord record, ReadOnlySpan<byte> signature, DateTimeOffset now)
    {
        Vault vault;
        using (var key = DeriveWrappingKey(signature, record.Challenge))
            vault = Vault.OpenWithWrappedKey(vaultPath, key.Span, Context, record.WrappedVaultKey);

        if (vault.VaultId != record.VaultId || ReadExpiry(vault, record) is not { } expiresAt || now >= expiresAt)
        {
            vault.Dispose();
            throw new VaultException(VaultErrorKind.QuickUnlockUnavailable,
                "Windows Hello の有効期限が切れました。安全のため、マスターパスワードでアンロックしてください。");
        }
        return vault;
    }

    public static DateTimeOffset? ReadExpiry(Vault vault, QuickUnlockRecord record)
    {
        using var plain = vault.OpenAuxiliary(ExpiryPurpose, record.Expiry);
        return plain is not null && DateTimeOffset.TryParse(Encoding.UTF8.GetString(plain.Span), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var at) ? at : null;
    }

    private static SealedBox SealExpiry(Vault vault, DateTimeOffset expiresAt) =>
        vault.SealAuxiliary(ExpiryPurpose, Encoding.UTF8.GetBytes(expiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));

    // ------------------------------------------------------------------ 端末への保存

    public static void Save(string path, QuickUnlockRecord record)
    {
        var dto = new QuickUnlockDto
        {
            Version = 1, VaultId = record.VaultId.ToString("D"), Challenge = record.Challenge,
            KeyNonce = record.WrappedVaultKey.Nonce, KeyCiphertext = record.WrappedVaultKey.Ciphertext,
            ExpiryNonce = record.Expiry.Nonce, ExpiryCiphertext = record.Expiry.Ciphertext,
        };
        AtomicFileStore.Write(path, JsonSerializer.SerializeToUtf8Bytes(dto, QuickUnlockJsonContext.Default.QuickUnlockDto), backupGenerations: 0);
    }

    public static QuickUnlockRecord? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var d = JsonSerializer.Deserialize(File.ReadAllBytes(path), QuickUnlockJsonContext.Default.QuickUnlockDto);
            if (d is not { Version: 1, Challenge.Length: ChallengeSize, KeyNonce: not null, KeyCiphertext: not null,
                    ExpiryNonce: not null, ExpiryCiphertext: not null } || !Guid.TryParse(d.VaultId, out var id))
                return null;
            return new QuickUnlockRecord(id, d.Challenge, new SealedBox(d.KeyNonce, d.KeyCiphertext), new SealedBox(d.ExpiryNonce, d.ExpiryCiphertext));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

internal sealed class QuickUnlockDto
{
    public int Version { get; set; }
    public string? VaultId { get; set; }
    public byte[]? Challenge { get; set; }
    public byte[]? KeyNonce { get; set; }
    public byte[]? KeyCiphertext { get; set; }
    public byte[]? ExpiryNonce { get; set; }
    public byte[]? ExpiryCiphertext { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(QuickUnlockDto))]
internal sealed partial class QuickUnlockJsonContext : JsonSerializerContext;

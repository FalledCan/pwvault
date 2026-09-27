using NSec.Cryptography;

namespace PwVault.Core.Crypto;

/// <summary>
/// 鍵の階層（要件 5.1）。
/// マスターパスワード --Argon2id--> マスター鍵 --HKDF(info=enc)--> KEK --AEAD--> 保管庫鍵
///                                               \-HKDF(info=auth)--> 認証キー（フェーズ2）
/// </summary>
public static class KeyHierarchy
{
    public const int MasterKeySize = 32;
    public const int AuthKeySize = 32;

    private static ReadOnlySpan<byte> InfoEnc => "pwvault/v1/enc"u8;
    private static ReadOnlySpan<byte> InfoAuth => "pwvault/v1/auth"u8;

    public static AeadAlgorithm Aead => AeadAlgorithm.XChaCha20Poly1305;

    /// <summary>マスターパスワードからマスター鍵を導出する。戻り値は呼び出し元が Dispose すること。</summary>
    public static SecretBuffer DeriveMasterKey(ReadOnlySpan<byte> passwordUtf8, KdfParameters kdf)
    {
        if (!kdf.IsValid)
            throw new ArgumentException("KDF パラメータが不正です。", nameof(kdf));

        var argon = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = kdf.Parallelism,
            MemorySize = kdf.MemoryKiB,
            NumberOfPasses = kdf.Iterations,
        });

        var masterKey = SecretBuffer.Allocate(MasterKeySize);
        try
        {
            argon.DeriveBytes(passwordUtf8, kdf.Salt, masterKey.Span);
            return masterKey;
        }
        catch
        {
            masterKey.Dispose();
            throw;
        }
    }

    /// <summary>
    /// マスター鍵から KEK を導出する。KEK はエクスポート不可の NSec Key として
    /// libsodium の保護メモリ（ページロック・破棄時ゼロ埋め）に置かれる。
    /// </summary>
    public static Key DeriveKek(ReadOnlySpan<byte> masterKey) =>
        KeyDerivationAlgorithm.HkdfSha256.DeriveKey(
            masterKey, ReadOnlySpan<byte>.Empty, InfoEnc, Aead,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.None });

    /// <summary>
    /// フェーズ2でサーバー認証に使うキー。KEK とは別の info で導出するので、
    /// この値がサーバーに渡っても KEK は逆算できない。
    /// </summary>
    public static SecretBuffer DeriveAuthKey(ReadOnlySpan<byte> masterKey)
    {
        var authKey = SecretBuffer.Allocate(AuthKeySize);
        KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(masterKey, ReadOnlySpan<byte>.Empty, InfoAuth, authKey.Span);
        return authKey;
    }

    /// <summary>パスワードから KEK までを一気に導出する。途中のマスター鍵はゼロ埋めして捨てる。</summary>
    public static Key DeriveKekFromPassword(string password, KdfParameters kdf)
    {
        using var pw = SecretBuffer.FromPassword(password);
        using var masterKey = DeriveMasterKey(pw.Span, kdf);
        return DeriveKek(masterKey.Span);
    }

    /// <summary>新しい保管庫鍵（256bit 乱数、SR-04）。ラップのためにエクスポートだけは許可する。</summary>
    public static Key CreateVaultKey() =>
        Key.Create(Aead, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    public static Key ImportVaultKey(ReadOnlySpan<byte> raw) =>
        Key.Import(Aead, raw, KeyBlobFormat.RawSymmetricKey,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
}

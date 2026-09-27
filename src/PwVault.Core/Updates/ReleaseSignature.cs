using System.Security.Cryptography;
using System.Text;
using NSec.Cryptography;

namespace PwVault.Core.Updates;

/// <summary>
/// リリースファイルの電子署名（Ed25519）。
/// 署名の対象は「版・ファイル名・SHA-256」をまとめた短い文字列なので、
/// 中身の差し替えだけでなく、古い版のファイルを新しい版として配る（ロールバック）ことや、別の OS 用のファイルとの取り違えも検知できる。
/// 署名用の秘密鍵は GitHub の Secrets にだけ置き、アプリには公開鍵だけを埋め込む。
/// </summary>
public static class ReleaseSignature
{
    /// <summary>リリースの署名を確かめる公開鍵（Ed25519・32 バイト・Base64）。</summary>
    public const string PublicKeyBase64 = "Iq66c5xeec4O6WpsUIHDT+567GJIIcLxYoVFoOmMmPo=";

    private static SignatureAlgorithm Alg => SignatureAlgorithm.Ed25519;

    public static byte[] Statement(string version, string assetName, ReadOnlySpan<byte> sha256) =>
        Encoding.UTF8.GetBytes($"pwvault-release-v1\n{version}\n{assetName}\n{Convert.ToHexStringLower(sha256)}\n");

    /// <summary>署名を作る（リリース用のワークフローでだけ使う）。</summary>
    public static string Sign(ReadOnlySpan<byte> privateKey, string version, string assetName, Stream content)
    {
        using var key = Key.Import(Alg, privateKey, KeyBlobFormat.RawPrivateKey);
        var signature = Alg.Sign(key, Statement(version, assetName, SHA256.HashData(content)));
        return Convert.ToBase64String(signature);
    }

    /// <summary>署名が正しいか。署名ファイルの中身は Base64（前後の空白は無視）。</summary>
    public static bool Verify(string version, string assetName, Stream content, string signatureBase64, string? publicKeyBase64 = null)
    {
        byte[] signature, publicKey;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
            publicKey = Convert.FromBase64String(publicKeyBase64 ?? PublicKeyBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        if (signature.Length != Alg.SignatureSize
            || !PublicKey.TryImport(Alg, publicKey, KeyBlobFormat.RawPublicKey, out var key) || key is null)
            return false;

        return Alg.Verify(key, Statement(version, assetName, SHA256.HashData(content)), signature);
    }

    /// <summary>新しい鍵の組を作る（初回と鍵の入れ替え時に使う）。秘密鍵は 32 バイトの種。</summary>
    public static (byte[] PrivateKey, byte[] PublicKey) GenerateKeyPair()
    {
        using var key = Key.Create(Alg, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (key.Export(KeyBlobFormat.RawPrivateKey), key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }
}

/// <summary>「v1.2.3」形式の版の比較。</summary>
public static class ReleaseVersion
{
    public static Version? Parse(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var plus = s.IndexOfAny(['+', '-']);
        if (plus >= 0) s = s[..plus];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    public static bool IsNewer(string? candidateTag, string? currentVersion) =>
        Parse(candidateTag) is { } candidate && Parse(currentVersion) is { } current && candidate > current;
}

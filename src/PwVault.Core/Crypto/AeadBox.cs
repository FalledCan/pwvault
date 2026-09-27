using System.Security.Cryptography;
using NSec.Cryptography;

namespace PwVault.Core.Crypto;

/// <summary>AEAD で暗号化したデータ（ナンスと暗号文＋認証タグ）。</summary>
public sealed record SealedBox(byte[] Nonce, byte[] Ciphertext);

/// <summary>XChaCha20-Poly1305 による暗号化・復号（SR-03）。ナンスは毎回 CSPRNG で 24 バイト生成する。</summary>
public static class AeadBox
{
    private static AeadAlgorithm Alg => KeyHierarchy.Aead;

    public static int NonceSize => Alg.NonceSize;

    public static SealedBox Seal(Key key, ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(Alg.NonceSize);
        var ciphertext = Alg.Encrypt(key, nonce, associatedData, plaintext);
        return new SealedBox(nonce, ciphertext);
    }

    /// <summary>
    /// 認証に成功すれば平文をピン留めバッファで返す（呼び出し元が Dispose してゼロ埋めすること）。
    /// 失敗（改ざん・鍵違い・形式不正）なら null。
    /// </summary>
    public static SecretBuffer? Open(Key key, ReadOnlySpan<byte> associatedData, SealedBox box)
    {
        if (box.Nonce.Length != Alg.NonceSize || box.Ciphertext.Length < Alg.TagSize)
            return null;

        var plaintext = SecretBuffer.Allocate(box.Ciphertext.Length - Alg.TagSize);
        if (Alg.Decrypt(key, box.Nonce, associatedData, box.Ciphertext, plaintext.Span))
            return plaintext;

        plaintext.Dispose();
        return null;
    }
}

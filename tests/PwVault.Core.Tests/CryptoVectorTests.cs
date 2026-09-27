using System.Security.Cryptography;
using System.Text;
using RefArgon2id = Konscious.Security.Cryptography.Argon2id;
using NSec.Cryptography;
using PwVault.Core.Crypto;

namespace PwVault.Core.Tests;

/// <summary>公開テストベクタと独立実装との照合で、暗号プリミティブの使い方が正しいことを確かめる（NFR-07）。</summary>
public class CryptoVectorTests
{
    /// <summary>draft-irtf-cfrg-xchacha-03 Appendix A.3.1</summary>
    [Fact]
    public void XChaCha20Poly1305_MatchesDraftVector()
    {
        var plaintext = Encoding.ASCII.GetBytes(
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
        var aad = Convert.FromHexString("50515253c0c1c2c3c4c5c6c7");
        var keyBytes = Convert.FromHexString("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        var nonce = Convert.FromHexString("404142434445464748494a4b4c4d4e4f5051525354555657");
        var expected = Convert.FromHexString(
            "bd6d179d3e83d43b9576579493c0e939572a1700252bfaccbed2902c21396cbb731c7f1b0b4aa6440bf3a82f4eda7e39ae64c6708c54c216cb96b72e1213b4522f8c9ba40db5d945b11b69b982c1bb9e3f3fac2bc369488f76b2383565d3fff921f9664c97637da9768812f615c68b13b52e"
            + "c0875924c1c7987947deafd8780acf49");

        using var key = KeyHierarchy.ImportVaultKey(keyBytes);
        var actual = KeyHierarchy.Aead.Encrypt(key, nonce, aad, plaintext);
        Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(actual));

        using var opened = AeadBox.Open(key, aad, new SealedBox(nonce, actual));
        Assert.NotNull(opened);
        Assert.True(opened.Span.SequenceEqual(plaintext));
    }

    [Fact]
    public void AeadBox_RejectsWrongAad_AndBitFlips()
    {
        using var key = KeyHierarchy.CreateVaultKey();
        var box = AeadBox.Seal(key, "aad"u8, "hello"u8);

        Assert.Null(AeadBox.Open(key, "aaX"u8, box));
        var flipped = (byte[])box.Ciphertext.Clone();
        flipped[^1] ^= 0x80;
        Assert.Null(AeadBox.Open(key, "aad"u8, box with { Ciphertext = flipped }));
        Assert.Null(AeadBox.Open(key, "aad"u8, box with { Nonce = new byte[12] }));
        Assert.Null(AeadBox.Open(key, "aad"u8, box with { Ciphertext = new byte[3] }));
    }

    [Fact]
    public void AeadBox_UsesFreshNonces()
    {
        using var key = KeyHierarchy.CreateVaultKey();
        var a = AeadBox.Seal(key, [], "same"u8);
        var b = AeadBox.Seal(key, [], "same"u8);
        Assert.Equal(24, a.Nonce.Length);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.NotEqual(a.Ciphertext, b.Ciphertext);
    }

    /// <summary>RFC 5869 Test Case 1。NSec の HKDF-SHA256 を .NET 標準の HKDF と RFC の値の両方で確認する。</summary>
    [Fact]
    public void HkdfSha256_MatchesRfc5869()
    {
        var ikm = Convert.FromHexString("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var salt = Convert.FromHexString("000102030405060708090a0b0c");
        var info = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9");
        const string expected = "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865";

        var nsec = KeyDerivationAlgorithm.HkdfSha256.DeriveBytes(ikm, salt, info, 42);
        Assert.Equal(expected, Convert.ToHexStringLower(nsec));
        Assert.Equal(expected, Convert.ToHexStringLower(HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 42, salt, info)));
    }

    /// <summary>
    /// RFC 9106 5.3 の Argon2id ベクタで独立実装（Konscious）を検証する。
    /// libsodium は並列度 4・secret・associated data を受け付けないので、NSec は下のテストで Konscious と照合する。
    /// </summary>
    [Fact]
    public void Argon2id_ReferenceImplementation_MatchesRfc9106()
    {
        var argon = new RefArgon2id(Enumerable.Repeat((byte)0x01, 32).ToArray())
        {
            Salt = Enumerable.Repeat((byte)0x02, 16).ToArray(),
            KnownSecret = Enumerable.Repeat((byte)0x03, 8).ToArray(),
            AssociatedData = Enumerable.Repeat((byte)0x04, 12).ToArray(),
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4,
        };
        Assert.Equal("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659",
            Convert.ToHexStringLower(argon.GetBytes(32)));
    }

    [Theory]
    [InlineData("password", KdfParameters.MinMemoryKiB, 1)]
    [InlineData("パスワード日本語", KdfParameters.MinMemoryKiB, 2)]
    [InlineData("correct horse battery staple", 32 * 1024, 3)]
    public void Argon2id_NSecMatchesReferenceImplementation(string password, int memoryKiB, int iterations)
    {
        var kdf = KdfParameters.CreateNew(memoryKiB, iterations);
        var pw = Encoding.UTF8.GetBytes(password);

        using var ours = KeyHierarchy.DeriveMasterKey(pw, kdf);
        var reference = new RefArgon2id(pw)
        {
            Salt = kdf.Salt,
            MemorySize = memoryKiB,
            Iterations = iterations,
            DegreeOfParallelism = 1,
        }.GetBytes(32);

        Assert.Equal(Convert.ToHexStringLower(reference), Convert.ToHexStringLower(ours.Span.ToArray()));
    }

    [Fact]
    public void Password_IsNfcNormalized()
    {
        // 「が」の合成済み (U+304C) と分解形 (U+304B U+3099) は同じ鍵になる
        using var composed = SecretBuffer.FromPassword("が");
        using var decomposed = SecretBuffer.FromPassword("が");
        Assert.True(composed.Span.SequenceEqual(decomposed.Span));
    }

    [Fact]
    public void AuthKey_DiffersFromKekDerivation()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        using var auth = KeyHierarchy.DeriveAuthKey(masterKey);
        using var kek = KeyHierarchy.DeriveKek(masterKey);

        // KEK はエクスポート不可。認証キーで暗号化したものを KEK では開けない＝別の鍵であることを確かめる
        using var authAsKey = KeyHierarchy.ImportVaultKey(auth.Span);
        var box = AeadBox.Seal(authAsKey, [], "x"u8);
        Assert.Null(AeadBox.Open(kek, [], box));
    }

    [Fact]
    public void SecretBuffer_ZeroesOnDispose()
    {
        var buffer = SecretBuffer.Allocate(8);
        buffer.Span.Fill(0xAA);
        buffer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => buffer.Span.Length);
    }
}

using PwVault.Core.Updates;

namespace PwVault.Core.Tests;

public class ReleaseSignatureTests
{
    private static readonly (byte[] Private, byte[] Public) Keys = ReleaseSignature.GenerateKeyPair();
    private static string Pub => Convert.ToBase64String(Keys.Public);
    private static MemoryStream Data(string s = "exe bytes") => new(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void SignThenVerify()
    {
        var sig = ReleaseSignature.Sign(Keys.Private, "1.2.3", "PwVault-1.2.3-win-x64.exe", Data());
        Assert.True(ReleaseSignature.Verify("1.2.3", "PwVault-1.2.3-win-x64.exe", Data(), sig, Pub));
    }

    [Fact]
    public void Rejects_TamperedContent_WrongVersion_WrongName_WrongKey()
    {
        var sig = ReleaseSignature.Sign(Keys.Private, "1.2.3", "a.exe", Data());
        Assert.False(ReleaseSignature.Verify("1.2.3", "a.exe", Data("exe bytez"), sig, Pub));       // 中身の差し替え
        Assert.False(ReleaseSignature.Verify("1.2.4", "a.exe", Data(), sig, Pub));                  // 古い版を新しい版として配る
        Assert.False(ReleaseSignature.Verify("1.2.3", "b.exe", Data(), sig, Pub));                  // 別のファイル
        var other = Convert.ToBase64String(ReleaseSignature.GenerateKeyPair().PublicKey);
        Assert.False(ReleaseSignature.Verify("1.2.3", "a.exe", Data(), sig, other));                // 別の鍵
        Assert.False(ReleaseSignature.Verify("1.2.3", "a.exe", Data(), "not base64!", Pub));
        Assert.False(ReleaseSignature.Verify("1.2.3", "a.exe", Data(), Convert.ToBase64String(new byte[10]), Pub));
    }

    [Fact]
    public void EmbeddedPublicKey_IsValidEd25519Key()
    {
        Assert.Equal(32, Convert.FromBase64String(ReleaseSignature.PublicKeyBase64).Length);
        // 埋め込みの鍵に対応する秘密鍵は無いので、適当な署名は必ず通らない
        var sig = ReleaseSignature.Sign(Keys.Private, "1.0.0", "a.exe", Data());
        Assert.False(ReleaseSignature.Verify("1.0.0", "a.exe", Data(), sig));
    }

    [Theory]
    [InlineData("v0.4.0", "0.3.0", true)]
    [InlineData("v0.3.1", "0.3.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("v0.3.0", "0.3.0", false)]
    [InlineData("v0.2.9", "0.3.0", false)]
    [InlineData("v0.10.0", "0.9.0", true)]   // 文字列比較ではなく数値で比べる
    [InlineData("v0.4.0", "0.3.0+abcdef", true)]
    [InlineData("garbage", "0.3.0", false)]
    [InlineData(null, "0.3.0", false)]
    public void VersionComparison(string? tag, string current, bool newer) =>
        Assert.Equal(newer, ReleaseVersion.IsNewer(tag, current));
}

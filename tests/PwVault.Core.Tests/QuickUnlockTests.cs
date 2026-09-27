using System.Security.Cryptography;
using PwVault.Core.QuickUnlock;

namespace PwVault.Core.Tests;

public class QuickUnlockTests
{
    private const string Password = "correct horse battery staple";
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Windows Hello の代わり: 端末に固定の秘密で challenge に「署名」する（同じ入力なら同じ出力）。</summary>
    private static byte[] FakeHelloSign(byte[] challenge, byte[]? deviceSecret = null) =>
        HMACSHA512.HashData(deviceSecret ?? "device-secret"u8.ToArray(), challenge).Concat(new byte[192]).ToArray();

    private static (string Path, Guid Id) CreateVault(TempDir dir)
    {
        var path = dir.File("vault.pwv");
        using var v = Vault.Create(path, Password, TestKdf.Fast());
        v.AddEntry(new EntryData { Title = "secret", Password = "s3cret" });
        v.Save(3);
        return (path, v.VaultId);
    }

    private static QuickUnlockRecord Enroll(string path, DateTimeOffset expiresAt)
    {
        using var vault = Vault.Open(path, Password);
        var challenge = QuickUnlockService.NewChallenge();
        return QuickUnlockService.Enroll(vault, challenge, FakeHelloSign(challenge), expiresAt);
    }

    [Fact]
    public void Enroll_Save_Load_Unlock()
    {
        using var dir = new TempDir();
        var (path, id) = CreateVault(dir);
        var record = Enroll(path, Now.AddDays(14));

        QuickUnlockService.Save(dir.File("quick.json"), record);
        var loaded = QuickUnlockService.Load(dir.File("quick.json"))!;
        Assert.Equal(id, loaded.VaultId);

        using var vault = QuickUnlockService.Unlock(path, loaded, FakeHelloSign(loaded.Challenge), Now);
        Assert.Equal("s3cret", Assert.Single(vault.GetEntries()).Data.Password);
    }

    [Fact]
    public void WrongSignature_IsRejected()
    {
        using var dir = new TempDir();
        var (path, _) = CreateVault(dir);
        var record = Enroll(path, Now.AddDays(14));

        var ex = Assert.Throws<VaultException>(() =>
            QuickUnlockService.Unlock(path, record, FakeHelloSign(record.Challenge, "other device"u8.ToArray()), Now));
        Assert.Equal(VaultErrorKind.QuickUnlockUnavailable, ex.Kind);
    }

    [Fact]
    public void ChangingMasterPassword_InvalidatesEnrollment()
    {
        using var dir = new TempDir();
        var (path, _) = CreateVault(dir);
        var record = Enroll(path, Now.AddDays(14));

        using (var vault = Vault.Open(path, Password))
        {
            vault.ChangeMasterPassword(Password, "a brand new passphrase");
            vault.Save(3);
        }

        var ex = Assert.Throws<VaultException>(() => QuickUnlockService.Unlock(path, record, FakeHelloSign(record.Challenge), Now));
        Assert.Equal(VaultErrorKind.QuickUnlockUnavailable, ex.Kind);
    }

    [Fact]
    public void Expired_RequiresMasterPassword_AndRefreshExtends()
    {
        using var dir = new TempDir();
        var (path, _) = CreateVault(dir);
        var record = Enroll(path, Now.AddDays(14));

        var ex = Assert.Throws<VaultException>(() => QuickUnlockService.Unlock(path, record, FakeHelloSign(record.Challenge), Now.AddDays(15)));
        Assert.Equal(VaultErrorKind.QuickUnlockUnavailable, ex.Kind);

        // マスターパスワードでアンロックしたら期限を延ばせる（Windows Hello の操作なし）
        using (var vault = Vault.Open(path, Password))
            record = QuickUnlockService.RefreshExpiry(vault, record, Now.AddDays(29));
        QuickUnlockService.Unlock(path, record, FakeHelloSign(record.Challenge), Now.AddDays(15)).Dispose();
    }

    [Fact]
    public void Expiry_CannotBeForged_ByEditingFile()
    {
        using var dir = new TempDir();
        var (path, _) = CreateVault(dir);
        var record = Enroll(path, Now.AddDays(1));

        // 別の保管庫で作った「遠い未来の期限」を差し込んでも通らない
        using var other = new TempDir();
        var (otherPath, _) = CreateVault(other);
        using var otherVault = Vault.Open(otherPath, Password);
        var forged = record with { Expiry = otherVault.SealAuxiliary(QuickUnlockService.ExpiryPurpose, "2099-01-01T00:00:00Z"u8) };

        var ex = Assert.Throws<VaultException>(() => QuickUnlockService.Unlock(path, forged, FakeHelloSign(record.Challenge), Now));
        Assert.Equal(VaultErrorKind.QuickUnlockUnavailable, ex.Kind);
    }

    [Fact]
    public void Load_RejectsGarbage()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("bad.json"), "{\"version\":1}");
        Assert.Null(QuickUnlockService.Load(dir.File("bad.json")));
        Assert.Null(QuickUnlockService.Load(dir.File("missing.json")));
    }

    [Fact]
    public void File_DoesNotContainVaultKeyInPlaintext()
    {
        using var dir = new TempDir();
        var (path, _) = CreateVault(dir);
        var record = Enroll(path, Now.AddDays(14));
        QuickUnlockService.Save(dir.File("quick.json"), record);
        var text = File.ReadAllText(dir.File("quick.json"));
        Assert.DoesNotContain("s3cret", text);
        Assert.Contains("key_ciphertext", text);
    }
}

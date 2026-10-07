using PwVault.Core.Crypto;
using PwVault.Core.Format;
using PwVault.Core.Storage;

namespace PwVault.Core.Tests;

public class VaultTests
{
    private const string Password = "correct horse battery staple";

    private static EntryData Sample(string title = "Example", string password = "s3cret!") => new()
    {
        Title = title,
        Username = "user@example.com",
        Password = password,
        Url = "https://example.com",
        Notes = "メモ",
        Tags = ["work"],
    };

    [Fact]
    public void Create_Save_Reopen_Decrypt_RoundTrips()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        Guid id;
        using (var vault = Vault.Create(path, Password, TestKdf.Fast()))
        {
            id = vault.AddEntry(Sample());
            vault.Save(backupGenerations: 3);
        }

        using var reopened = Vault.Open(path, Password);
        var entry = Assert.Single(reopened.GetEntries());
        Assert.Equal(id, entry.Id);
        Assert.Equal("Example", entry.Data.Title);
        Assert.Equal("s3cret!", entry.Data.Password);
        Assert.Equal(["work"], entry.Data.Tags);
        Assert.Equal(1, entry.Revision);
    }

    [Fact]
    public void File_DoesNotContainPlaintext()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using (var vault = Vault.Create(path, Password, TestKdf.Fast()))
        {
            vault.AddEntry(Sample("VeryUniqueTitle", "VeryUniquePassword"));
            vault.Save(3);
        }

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("VeryUniqueTitle", text);
        Assert.DoesNotContain("VeryUniquePassword", text);
        Assert.DoesNotContain("example.com", text);
        Assert.DoesNotContain(Password, text);
    }

    [Fact]
    public void Open_WithWrongPassword_ThrowsWrongPassword()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        Vault.Create(path, Password, TestKdf.Fast()).Dispose();

        var ex = Assert.Throws<VaultException>(() => Vault.Open(path, "wrong password!!"));
        Assert.Equal(VaultErrorKind.WrongPassword, ex.Kind);
    }

    [Fact]
    public void Tampered_KdfParameters_AreDetected()
    {
        var (doc, _) = CreateDocument();
        var weakened = new KdfParameters(doc.Header.Kdf.MemoryKiB, doc.Header.Kdf.Iterations + 1, 1, doc.Header.Kdf.Salt);
        var tampered = doc with { Header = CopyHeader(doc.Header, kdf: weakened) };

        // 攻撃者がチェックサムも再計算した想定（Serialize で再計算される）
        var bytes = VaultFileCodec.Serialize(tampered);
        var ex = Assert.Throws<VaultException>(() => Vault.Open("x", VaultFileCodec.Deserialize(bytes), Password));
        Assert.Equal(VaultErrorKind.WrongPassword, ex.Kind);
    }

    [Fact]
    public void Tampered_VaultIdInHeader_IsDetectedByWrapAad()
    {
        var (doc, _) = CreateDocument();
        var tampered = doc with { Header = CopyHeader(doc.Header, vaultId: Guid.NewGuid()) };
        Assert.Throws<VaultException>(() => Vault.Open("x", tampered, Password));
    }

    [Fact]
    public void Tampered_EntryCiphertext_IsDetected()
    {
        var (doc, _) = CreateDocument();
        var e = doc.Entries[0];
        var ct = (byte[])e.Box.Ciphertext.Clone();
        ct[0] ^= 0x01;
        var tampered = doc.WithEntries([e.With(box: e.Box with { Ciphertext = ct }), .. doc.Entries.Skip(1)]);

        var ex = Assert.Throws<VaultException>(() => Vault.Open("x", tampered, Password));
        Assert.Equal(VaultErrorKind.Tampered, ex.Kind);
    }

    [Fact]
    public void Swapped_EntryIds_AreDetected()
    {
        var (doc, _) = CreateDocument();
        var a = doc.Entries[0];
        var b = doc.Entries[1];
        var tampered = doc.WithEntries([a.With(id: b.Id), b.With(id: a.Id)]);

        var ex = Assert.Throws<VaultException>(() => Vault.Open("x", tampered, Password));
        Assert.Equal(VaultErrorKind.Tampered, ex.Kind);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("updated_at")]
    [InlineData("deleted")]
    public void Tampered_PlaintextMetadata_IsDetected(string field)
    {
        var (doc, _) = CreateDocument();
        var e = doc.Entries[0];
        var changed = field switch
        {
            "revision" => e.With(revision: e.Revision + 5),
            "updated_at" => e.With(updatedAt: "2000-01-01T00:00:00.000Z"),
            _ => e.With(deleted: !e.Deleted),
        };
        var tampered = doc.WithEntries([changed, .. doc.Entries.Skip(1)]);

        var ex = Assert.Throws<VaultException>(() => Vault.Open("x", tampered, Password));
        Assert.Equal(VaultErrorKind.Tampered, ex.Kind);
    }

    [Fact]
    public void Entry_FromAnotherVault_IsDetected()
    {
        var (doc1, _) = CreateDocument();
        var (doc2, _) = CreateDocument();
        var tampered = doc1.WithEntries([.. doc1.Entries, doc2.Entries[0]]);

        var ex = Assert.Throws<VaultException>(() => Vault.Open("x", tampered, Password));
        Assert.Equal(VaultErrorKind.Tampered, ex.Kind);
    }

    [Fact]
    public void AccidentalCorruption_IsReportedAsCorrupted_NotWrongPassword()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using (var vault = Vault.Create(path, Password, TestKdf.Fast()))
        {
            vault.AddEntry(Sample());
            vault.Save(3);
        }

        // Base64 の暗号文の 1 文字を別の有効な文字に置き換える（JSON としては正しいまま）
        var text = File.ReadAllText(path);
        var idx = text.IndexOf("\"ciphertext\": \"", StringComparison.Ordinal) + 20;
        var c = text[idx] == 'A' ? 'B' : 'A';
        File.WriteAllText(path, text[..idx] + c + text[(idx + 1)..]);

        var ex = Assert.Throws<VaultException>(() => Vault.Open(path, Password));
        Assert.Equal(VaultErrorKind.Corrupted, ex.Kind);
        Assert.True(ex.SuggestsBackupRestore);
    }

    [Fact]
    public void GarbageFile_IsInvalidFormat()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        File.WriteAllText(path, "not a vault");

        var ex = Assert.Throws<VaultException>(() => Vault.Open(path, Password));
        Assert.Equal(VaultErrorKind.InvalidFormat, ex.Kind);
    }

    [Fact]
    public void TruncatedFile_IsInvalidFormat()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        Vault.Create(path, Password, TestKdf.Fast()).Dispose();
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        var ex = Assert.Throws<VaultException>(() => Vault.Open(path, Password));
        Assert.Equal(VaultErrorKind.InvalidFormat, ex.Kind);
    }

    [Fact]
    public void NewerFormatVersion_IsRejected()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        Vault.Create(path, Password, TestKdf.Fast()).Dispose();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"format_version\": 1", "\"format_version\": 99"));

        var ex = Assert.Throws<VaultException>(() => Vault.Open(path, Password));
        Assert.Equal(VaultErrorKind.UnsupportedVersion, ex.Kind);
    }

    [Fact]
    public void ChangeMasterPassword_RotatesVaultKey_AndKeepsEveryEntry()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        EncryptedEntry[] before;
        Guid trashed, purged;
        using (var vault = Vault.Create(path, Password, TestKdf.Fast()))
        {
            vault.AddEntry(Sample());
            trashed = vault.AddEntry(Sample("ゴミ箱"));
            vault.MoveToTrash(trashed);
            purged = vault.AddEntry(Sample("完全削除"));
            vault.Purge(purged);
            vault.Save(3);
            before = [.. VaultFileCodec.Deserialize(File.ReadAllBytes(path)).Entries];

            Assert.Throws<VaultException>(() => vault.ChangeMasterPassword("wrong", "new password 123"));
            vault.ChangeMasterPassword(Password, "new password 123");
            vault.Save(3);
        }

        var ex = Assert.Throws<VaultException>(() => Vault.Open(path, Password));
        Assert.Equal(VaultErrorKind.WrongPassword, ex.Kind);

        using var reopened = Vault.Open(path, "new password 123");
        Assert.Equal("s3cret!", reopened.GetEntries().Single(e => e.Data.Title == "Example").Data.Password);
        Assert.True(reopened.GetEntry(trashed)!.Data.IsTrashed);
        Assert.Null(reopened.GetEntry(purged));

        // 保管庫鍵が新しくなったので、全エントリ（墓標も）の暗号文が変わる。リビジョン・更新日時・削除フラグはそのまま
        var after = VaultFileCodec.Deserialize(File.ReadAllBytes(path)).Entries.ToDictionary(e => e.Id);
        Assert.Equal(before.Length, after.Count);
        foreach (var b in before)
        {
            var a = after[b.Id];
            Assert.NotEqual(b.Box.Ciphertext, a.Box.Ciphertext);
            Assert.Equal((b.Revision, b.UpdatedAt, b.Deleted), (a.Revision, a.UpdatedAt, a.Deleted));
        }
    }

    [Fact]
    public void Attack_LeakedOldPassword_CannotReadEntriesSavedAfterChange()
    {
        // 漏れたのでマスターパスワードを変えた。攻撃者は漏れたパスワードと古いファイル（クラウドの版の履歴・.bak）を持っている
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        const string leaked = "old password that leaked!!", fresh = "brand new strong password";
        using (var vault = Vault.Create(path, leaked, TestKdf.Fast()))
        {
            vault.AddEntry(Sample("before"));
            vault.Save(3);
        }
        var oldFile = VaultFileCodec.Deserialize(File.ReadAllBytes(path));
        using (var vault = Vault.Open(path, leaked))
        {
            vault.ChangeMasterPassword(leaked, fresh);
            vault.AddEntry(Sample("after", "secret-after-change"));
            vault.Save(3);
        }

        // 新しいファイルのエントリに、古いヘッダ（漏れたパスワードで保管庫鍵を取り出せる）を付けても読めない
        var newFile = VaultFileCodec.Deserialize(File.ReadAllBytes(path));
        var forged = dir.File("forged.pwv");
        File.WriteAllBytes(forged, VaultFileCodec.Serialize(newFile with { Header = oldFile.Header }));
        Assert.Equal(VaultErrorKind.Tampered, Assert.Throws<VaultException>(() => Vault.Open(forged, leaked)).Kind);
    }

    [Fact]
    public void ChangeKdfParameters_KeepsPassword()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using (var vault = Vault.Create(path, Password, TestKdf.Fast()))
        {
            vault.ChangeMasterPassword(Password, Password, memoryKiB: KdfParameters.MinMemoryKiB, iterations: 2);
            vault.Save(3);
        }

        using var reopened = Vault.Open(path, Password);
        Assert.Equal(2, reopened.Kdf.Iterations);
    }

    [Fact]
    public void Update_IncrementsRevision_AndRecordsPasswordHistory()
    {
        using var vault = Vault.CreateInMemory("x", Password, TestKdf.Fast());
        var id = vault.AddEntry(Sample(password: "old-pass"));
        var data = vault.GetEntry(id)!.Data;
        data.Password = "new-pass";
        vault.UpdateEntry(id, data);

        var entry = vault.GetEntry(id)!;
        Assert.Equal(2, entry.Revision);
        Assert.Equal("new-pass", entry.Data.Password);
        Assert.Equal("old-pass", Assert.Single(entry.Data.History).Password);

        // パスワード以外の変更では履歴は増えない
        entry.Data.Title = "renamed";
        vault.UpdateEntry(id, entry.Data);
        Assert.Single(vault.GetEntry(id)!.Data.History);
    }

    [Fact]
    public void Trash_Restore_Purge_LeavesTombstone()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        Guid keep, gone;
        using (var vault = Vault.Create(path, Password, TestKdf.Fast()))
        {
            keep = vault.AddEntry(Sample("keep"));
            gone = vault.AddEntry(Sample("gone"));

            vault.MoveToTrash(keep);
            Assert.True(vault.GetEntry(keep)!.Data.IsTrashed);
            vault.RestoreFromTrash(keep);
            Assert.False(vault.GetEntry(keep)!.Data.IsTrashed);

            vault.MoveToTrash(gone);
            vault.EmptyTrash();
            Assert.Null(vault.GetEntry(gone));
            vault.Save(3);
        }

        var doc = VaultFileCodec.Deserialize(File.ReadAllBytes(path));
        var tombstone = Assert.Single(doc.Entries, e => e.Id == gone);
        Assert.True(tombstone.Deleted);
        Assert.Equal(3, tombstone.Revision); // 追加 1 → ゴミ箱 2 → 墓標 3
        using var reopened = Vault.Open(path, Password);
        Assert.Equal(keep, Assert.Single(reopened.GetEntries()).Id);
    }

    [Fact]
    public void Dispose_Locks_TheVault()
    {
        var vault = Vault.CreateInMemory("x", Password, TestKdf.Fast());
        vault.AddEntry(Sample());
        vault.Dispose();
        Assert.True(vault.IsLocked);
        Assert.Throws<InvalidOperationException>(() => vault.GetEntries());
    }

    [Fact]
    public void Save_KeepsBackupGenerations()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using var vault = Vault.Create(path, Password, TestKdf.Fast()); // 1 回目の保存（バックアップなし）
        for (var i = 0; i < 5; i++)
        {
            vault.AddEntry(Sample($"e{i}"));
            vault.Save(backupGenerations: 3);
        }

        Assert.Equal(3, AtomicFileStore.ListBackups(path).Count);
        // .bak1 は直前の版（エントリ 4 件）で、パスワードで開ける
        using var bak = Vault.Open(AtomicFileStore.BackupPath(Path.GetFullPath(path), 1), Password);
        Assert.Equal(4, bak.GetEntries().Count);

        vault.Save(backupGenerations: 1);
        Assert.Single(AtomicFileStore.ListBackups(path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
    }

    [Fact]
    public void Create_RefusesToOverwriteExistingFile()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        File.WriteAllText(path, "existing");
        Assert.Throws<VaultException>(() => Vault.Create(path, Password, TestKdf.Fast()));
        Assert.Equal("existing", File.ReadAllText(path));
    }

    [Fact]
    public void VerifyPassword_Works()
    {
        using var vault = Vault.CreateInMemory("x", Password, TestKdf.Fast());
        Assert.True(vault.VerifyPassword(Password));
        Assert.False(vault.VerifyPassword(Password + "x"));
    }

    [Fact]
    public void AuthKey_IsDeterministic_AndDependsOnPassword()
    {
        using var vault = Vault.CreateInMemory("x", Password, TestKdf.Fast());
        using var a = vault.DeriveAuthKey(Password);
        using var b = vault.DeriveAuthKey(Password);
        using var c = vault.DeriveAuthKey(Password + "!");
        Assert.True(a.Span.SequenceEqual(b.Span));
        Assert.False(a.Span.SequenceEqual(c.Span));
    }

    private static (VaultDocument Doc, Guid[] Ids) CreateDocument()
    {
        using var vault = Vault.CreateInMemory("x", Password, TestKdf.Fast());
        var ids = new[] { vault.AddEntry(Sample("a")), vault.AddEntry(Sample("b")) };
        return (VaultFileCodec.Deserialize(vault.ToBytes()), ids);
    }

    private static VaultHeader CopyHeader(VaultHeader h, KdfParameters? kdf = null, Guid? vaultId = null) => new()
    {
        FormatVersion = h.FormatVersion,
        VaultId = vaultId ?? h.VaultId,
        Kdf = kdf ?? h.Kdf,
        WrappedVaultKey = h.WrappedVaultKey,
    };
}

using PwVault.Core.Format;

namespace PwVault.Core.Tests;

/// <summary>
/// 同期フォルダ（Google ドライブ・Nextcloud など）で、複数の端末が同じ保管庫ファイルを使うときの取り込み（合体）。
/// 1 つのファイルを 2 つの Vault インスタンスで開き、2 台の PC に見立てる。
/// </summary>
public class SyncTests
{
    private const string Password = "correct horse battery staple";

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static EntryData Entry(string title, string password = "pw") =>
        new() { Title = title, Username = "user", Password = password, Url = "https://example.com" };

    private static string[] Titles(Vault v) => v.GetEntries().Select(e => e.Data.Title).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void TwoDevices_AddDifferentEntries_BothAreKept()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using var pc1 = Vault.Create(path, Password, TestKdf.Fast());
        using var pc2 = Vault.Open(path, Password);

        pc1.AddEntry(Entry("PC1 で追加"));
        pc1.Save(3);
        pc2.AddEntry(Entry("PC2 で追加"));
        pc2.Save(3); // 保存の直前に PC1 の変更を取り込むので、PC1 の追加は消えない

        using (var reopened = Vault.Open(path, Password))
            Assert.Equal(["PC1 で追加", "PC2 で追加"], Titles(reopened));

        // PC1 は、PC2 が書いたファイルを取り込むと両方そろう。取り込み後は書き戻す必要がない
        Assert.True(pc1.SyncFromDisk());
        Assert.Equal(["PC1 で追加", "PC2 で追加"], Titles(pc1));
        Assert.False(pc1.IsDirty);
        Assert.False(pc1.SyncFromDisk()); // 変わっていなければ何もしない
    }

    [Fact]
    public void SameEntryEditedOnBoth_NewerWins_AndLosingPasswordGoesToHistory()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        var clock1 = new Clock();
        var clock2 = new Clock();
        Guid id;
        using (var v = Vault.Create(path, Password, TestKdf.Fast(), clock: clock1))
        {
            id = v.AddEntry(Entry("共通", "original"));
            v.Save(3);
        }
        using var pc1 = Vault.Open(path, Password, clock1);
        using var pc2 = Vault.Open(path, Password, clock2);

        clock1.Now = clock1.Now.AddMinutes(1);
        pc1.UpdateEntry(id, Entry("共通", "from-pc1"));
        pc1.Save(3);

        clock2.Now = clock2.Now.AddMinutes(2); // PC2 の方が後から変えた
        pc2.UpdateEntry(id, Entry("共通", "from-pc2"));
        pc2.Save(3);

        using var reopened = Vault.Open(path, Password);
        var entry = reopened.GetEntry(id)!;
        Assert.Equal("from-pc2", entry.Data.Password);
        // 負けた PC1 のパスワードも、元のパスワードも履歴に残る
        Assert.Contains(entry.Data.History, h => h.Password == "from-pc1");
        Assert.Contains(entry.Data.History, h => h.Password == "original");

        // PC1 も同じ結果に落ち着く
        pc1.SyncFromDisk();
        Assert.Equal("from-pc2", pc1.GetEntry(id)!.Data.Password);
        Assert.False(pc1.IsDirty);
    }

    [Fact]
    public void MoreEdits_WinOverOlderRevision_EvenIfClockIsLater()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        Guid id;
        using (var v = Vault.Create(path, Password, TestKdf.Fast()))
        {
            id = v.AddEntry(Entry("共通"));
            v.Save(3);
        }
        using var pc1 = Vault.Open(path, Password);
        pc1.UpdateEntry(id, Entry("PC1 で 1 回目"));
        pc1.Save(3);

        using var pc2 = Vault.Open(path, Password); // PC1 の変更を見てから変える
        pc2.UpdateEntry(id, Entry("PC2 が上書き"));
        pc2.Save(3);

        pc1.SyncFromDisk();
        Assert.Equal("PC2 が上書き", pc1.GetEntry(id)!.Data.Title);
    }

    [Fact]
    public void Purge_OnOneDevice_IsReflectedOnTheOther()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using var pc1 = Vault.Create(path, Password, TestKdf.Fast());
        var id = pc1.AddEntry(Entry("消すもの"));
        pc1.AddEntry(Entry("残すもの"));
        pc1.Save(3);
        using var pc2 = Vault.Open(path, Password);

        pc1.Purge(id);
        pc1.Save(3);

        Assert.True(pc2.SyncFromDisk());
        Assert.Null(pc2.GetEntry(id));
        Assert.Equal(["残すもの"], Titles(pc2));
    }

    [Fact]
    public void MasterPasswordChangedOnOtherDevice_IsAdopted()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        const string newPassword = "a brand new master password";
        using var pc1 = Vault.Create(path, Password, TestKdf.Fast());
        using var pc2 = Vault.Open(path, Password);

        pc1.ChangeMasterPassword(Password, newPassword);
        pc1.Save(3);

        pc2.AddEntry(Entry("PC2 で追加"));
        pc2.Save(3); // PC1 の新しいヘッダを取り込んでから書く（古いパスワードで書き戻さない）

        Assert.Throws<VaultException>(() => Vault.Open(path, Password).Dispose());
        using var reopened = Vault.Open(path, newPassword);
        Assert.Equal(["PC2 で追加"], Titles(reopened));
        Assert.True(pc2.VerifyPassword(newPassword));
    }

    [Fact]
    public void TamperedEntryInFile_IsRejected_AndNothingIsMerged()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using var pc1 = Vault.Create(path, Password, TestKdf.Fast());
        pc1.AddEntry(Entry("正規"));
        pc1.Save(3);

        // ファイルを書き換えられる攻撃者が、リビジョンを上げたエントリを差し込む（チェックサムは再計算）
        var doc = VaultFileCodec.Deserialize(File.ReadAllBytes(path));
        var e = doc.Entries[0];
        var forged = doc.WithEntries([e.With(revision: e.Revision + 10)]);
        File.WriteAllBytes(path, VaultFileCodec.Serialize(forged));

        var ex = Assert.Throws<VaultException>(() => pc1.SyncFromDisk());
        Assert.Equal(VaultErrorKind.Tampered, ex.Kind);
        Assert.Equal(1, pc1.GetEntries().Single().Revision);
        Assert.Throws<VaultException>(() => pc1.Save(3)); // 改ざんされたファイルには書き込まない（.bak から戻してもらう）
    }

    [Fact]
    public void ConflictCopies_AreMerged_AndOnlyConflictNamesAreDetected()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using var pc1 = Vault.Create(path, Password, TestKdf.Fast());
        pc1.AddEntry(Entry("本体"));
        pc1.Save(3);

        // 同期アプリが、同時に書かれたもう片方を別名で残した
        var copy = dir.File("vault (conflicted copy 2026-09-29 120000).pwv");
        File.Copy(path, copy);
        using (var other = Vault.Open(copy, Password))
        {
            other.AddEntry(Entry("競合コピー側"));
            other.Save(0);
        }
        // 競合コピーではないもの（手動のバックアップ・世代バックアップ・アイコン・別の名前）
        File.Copy(path, dir.File("vault-backup-20260929.pwv"));
        File.Copy(path, dir.File("other (1).pwv"));
        File.WriteAllText(dir.File("vault.pwv.icons"), "{}");
        File.Copy(path, dir.File("vault (1).pwv"));
        File.Copy(path, dir.File("vault.sync-conflict-20260929-ABC.pwv"));

        var found = Vault.FindConflictCopies(path).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["vault (1).pwv", "vault (conflicted copy 2026-09-29 120000).pwv", "vault.sync-conflict-20260929-ABC.pwv"], found);

        var merged = pc1.MergeConflictCopies();
        Assert.Equal(3, merged.Count);
        Assert.True(pc1.IsDirty);
        pc1.Save(3);
        using var reopened = Vault.Open(path, Password);
        Assert.Equal(["本体", "競合コピー側"], Titles(reopened));
    }

    [Fact]
    public void HugeFiles_AreNotRead()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using var pc1 = Vault.Create(path, Password, TestKdf.Fast());
        pc1.AddEntry(Entry("本体"));
        pc1.Save(3);

        // 同期フォルダに、競合コピーの名前で巨大なファイルを置かれた（中身は空の疎ファイル）
        var huge = dir.File("vault (1).pwv");
        using (var fs = new FileStream(huge, FileMode.CreateNew)) fs.SetLength(Vault.MaxFileBytes + 1);
        Assert.Empty(pc1.MergeConflictCopies());
        Assert.True(File.Exists(huge)); // 取り込めないものは消さない

        // 本体が巨大なファイルに置き換えられた
        File.Delete(path);
        File.Move(huge, path);
        var ex = Assert.Throws<VaultException>(() => pc1.SyncFromDisk());
        Assert.Equal(VaultErrorKind.InvalidFormat, ex.Kind);
        Assert.Equal(VaultErrorKind.InvalidFormat, Assert.Throws<VaultException>(() => Vault.Open(path, Password)).Kind);
    }

    [Fact]
    public void MoveTo_WritesToNewPlace_MergesSameVault_RefusesOtherVault()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        using var pc1 = Vault.Create(path, Password, TestKdf.Fast());
        pc1.AddEntry(Entry("PC1"));
        pc1.Save(3);

        // 同じ保管庫が既にクラウドにある（別の PC が先に移していた）
        var cloud = dir.File(Path.Combine("cloud", "PwVault", "vault.pwv"));
        Directory.CreateDirectory(Path.GetDirectoryName(cloud)!);
        File.Copy(path, cloud);
        using (var pc2 = Vault.Open(cloud, Password))
        {
            pc2.AddEntry(Entry("PC2"));
            pc2.Save(3);
        }

        pc1.MoveTo(cloud, 3);
        Assert.Equal(Path.GetFullPath(cloud), pc1.FilePath);
        Assert.Equal(["PC1", "PC2"], Titles(pc1));
        Assert.True(File.Exists(path)); // 元のファイルは消さない（消すのはアプリ側）

        // 別の保管庫がある場所には移さない
        var otherPath = dir.File("other.pwv");
        Vault.Create(otherPath, Password, TestKdf.Fast()).Dispose();
        var ex = Assert.Throws<VaultException>(() => pc1.MoveTo(otherPath, 3));
        Assert.Equal(VaultErrorKind.DifferentVault, ex.Kind);
        Assert.Equal(Path.GetFullPath(cloud), pc1.FilePath);
    }
}

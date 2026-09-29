using PwVault.Core.Tools;

namespace PwVault.Core.Tests;

public class AutoTypeMatcherTests
{
    [Theory]
    [InlineData("ffxivboot.exe", "ffxivboot")]
    [InlineData("FFXIVBoot.EXE", "ffxivboot")]
    [InlineData(@"C:\Program Files\SquareEnix\ffxivboot.exe", "ffxivboot")]
    [InlineData("\"ffxivboot.exe\"", "ffxivboot")]
    [InlineData("  launcher ", "launcher")]
    public void Normalize_IgnoresCaseFolderAndExe(string input, string expected) =>
        Assert.Equal(expected, AutoTypeMatcher.Normalize(input));

    [Fact]
    public void Parse_SplitsAndRemovesDuplicates()
    {
        Assert.Equal(["ffxivboot.exe", "game.exe"], AutoTypeMatcher.Parse("ffxivboot.exe, FFXIVBOOT, game.exe、 ,"));
        Assert.Empty(AutoTypeMatcher.Parse(" , "));
    }

    [Fact]
    public void Linked_MatchesOnlyLinkedAndNotTrashed()
    {
        var linked = new VaultEntry(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, new EntryData { Title = "FF14", AutoTypeApps = ["ffxivboot.exe"] });
        var trashed = new VaultEntry(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, new EntryData { Title = "old", AutoTypeApps = ["ffxivboot"], TrashedAt = DateTimeOffset.UtcNow });
        var other = new VaultEntry(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, new EntryData { Title = "bank" });

        var found = AutoTypeMatcher.Linked([linked, trashed, other], @"C:\Games\FFXIVBoot.exe");
        Assert.Equal([linked], found);
        Assert.Empty(AutoTypeMatcher.Linked([linked], ""));
    }

    [Fact]
    public void Sequence_TypesIdTabPassword_OrPasswordOnly_NeverEnter()
    {
        var data = new EntryData { Username = "taro", Password = "pw" };
        Assert.Equal<AutoTypeAction>([new AutoTypeAction.Text("taro"), new AutoTypeAction.Tab(), new AutoTypeAction.Text("pw")], AutoTypeMatcher.Sequence(data));

        data.AutoType = AutoTypeMode.PasswordOnly;
        Assert.Equal<AutoTypeAction>([new AutoTypeAction.Text("pw")], AutoTypeMatcher.Sequence(data));

        // ID が空なら Tab も打たない
        Assert.Equal<AutoTypeAction>([new AutoTypeAction.Text("pw")], AutoTypeMatcher.Sequence(new EntryData { Password = "pw" }));
    }

    [Fact]
    public void AutoTypeSettings_AreSavedEncryptedWithTheEntry()
    {
        using var dir = new TempDir();
        var path = dir.File("vault.pwv");
        Guid id;
        using (var v = Vault.Create(path, "correct horse battery staple", TestKdf.Fast()))
        {
            id = v.AddEntry(new EntryData { Title = "FF14", AutoTypeApps = ["ffxivboot.exe"], AutoType = AutoTypeMode.PasswordOnly });
            v.Save(0);
        }
        Assert.DoesNotContain("ffxivboot", File.ReadAllText(path)); // どのゲームを使っているかもファイルからは読めない
        using var reopened = Vault.Open(path, "correct horse battery staple");
        var data = reopened.GetEntry(id)!.Data;
        Assert.Equal(["ffxivboot.exe"], data.AutoTypeApps);
        Assert.Equal(AutoTypeMode.PasswordOnly, data.AutoType);
    }
}

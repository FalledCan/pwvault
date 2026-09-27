using System.Diagnostics;
using PwVault.Core.Interop;
using PwVault.Core.Tools;

namespace PwVault.Core.Tests;

public class PasswordGeneratorTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(128)]
    public void Generates_RequestedLength_WithEveryClass(int length)
    {
        for (var n = 0; n < 50; n++)
        {
            var pw = PasswordGenerator.Generate(new GeneratorOptions { Length = length });
            Assert.Equal(length, pw.Length);
            Assert.Contains(pw, char.IsUpper);
            Assert.Contains(pw, char.IsLower);
            Assert.Contains(pw, char.IsDigit);
            Assert.Contains(pw, c => PasswordGenerator.SymbolChars.Contains(c));
        }
    }

    [Fact]
    public void ExcludeAmbiguous_RemovesConfusingCharacters()
    {
        var opts = new GeneratorOptions { Length = 128, ExcludeAmbiguous = true };
        for (var n = 0; n < 50; n++)
            Assert.DoesNotContain(PasswordGenerator.Generate(opts), c => PasswordGenerator.AmbiguousChars.Contains(c));
    }

    [Fact]
    public void DigitsOnly_Works()
    {
        var pw = PasswordGenerator.Generate(new GeneratorOptions { Length = 12, Uppercase = false, Lowercase = false, Symbols = false });
        Assert.All(pw, c => Assert.True(char.IsDigit(c)));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(129)]
    public void RejectsOutOfRangeLength(int length) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(new GeneratorOptions { Length = length }));

    [Fact]
    public void RejectsNoCharacterClasses() =>
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(new GeneratorOptions
            { Uppercase = false, Lowercase = false, Digits = false, Symbols = false }));

    [Fact]
    public void Output_IsNotObviouslyBiased()
    {
        // 数字のみ 10 万文字で各数字の出現率が 10% ± 1% に収まること
        var counts = new int[10];
        for (var n = 0; n < 1000; n++)
            foreach (var c in PasswordGenerator.Generate(new GeneratorOptions { Length = 100, Uppercase = false, Lowercase = false, Symbols = false }))
                counts[c - '0']++;
        Assert.All(counts, c => Assert.InRange(c, 9000, 11000));
    }
}

public class PasswordStrengthTests
{
    [Theory]
    [InlineData("password")]
    [InlineData("Password123")]
    [InlineData("qwertyuiop")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("123456789012")]
    public void CommonPatterns_AreWeak(string pw) => Assert.True(PasswordStrength.Evaluate(pw).IsWeak);

    [Theory]
    [InlineData("k#8Vq!2mZr$9Lx@4")]
    [InlineData("correct-horse-battery-staple-orbit")]
    public void RandomOrLongPasswords_AreStrong(string pw) =>
        Assert.True(PasswordStrength.Evaluate(pw).Level >= StrengthLevel.Strong);

    [Fact]
    public void TwoCharacterCoincidences_AreNotFlagged()
    {
        // "tt"（battery）や "ba" は普通の単語に現れるので警告しない
        var r = PasswordStrength.Evaluate("Tr0ub4dor&3-horse-battery");
        Assert.DoesNotContain(r.Warnings, w => w.Contains("繰り返し") || w.Contains("連番"));
        Assert.Contains(PasswordStrength.Evaluate("xyzzy-abc-4821-kqp").Warnings, w => w.Contains("連番"));
        Assert.Contains(PasswordStrength.Evaluate("Kp#aaaa9-Lm2!").Warnings, w => w.Contains("繰り返し"));
    }

    [Fact]
    public void MasterPasswordMinimum_Is12Characters()
    {
        Assert.False(PasswordStrength.MeetsMasterPasswordMinimum("12345678901"));
        Assert.True(PasswordStrength.MeetsMasterPasswordMinimum("123456789012"));
    }
}

public class HealthAndSearchTests
{
    private static VaultEntry E(string title, string password = "k#8Vq!2mZr$9Lx@4", string user = "", string url = "",
        string[]? tags = null, bool favorite = false, bool trashed = false) =>
        new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, new EntryData
        {
            Title = title, Password = password, Username = user, Url = url, Tags = [.. tags ?? []],
            Favorite = favorite, TrashedAt = trashed ? DateTimeOffset.UtcNow : null,
        });

    [Fact]
    public void Health_FindsWeakAndReused_IgnoringTrash()
    {
        var weak = E("weak", "password1");
        var r1 = E("r1", "Shared#Secret#2026!");
        var r2 = E("r2", "Shared#Secret#2026!");
        var trashed = E("t", "Shared#Secret#2026!", trashed: true);
        var ok = E("ok");

        var report = PasswordHealth.Analyze([weak, r1, r2, trashed, ok]);
        Assert.Contains(weak.Id, report.Weak);
        Assert.DoesNotContain(ok.Id, report.Weak);
        Assert.Equal(new HashSet<Guid> { r1.Id, r2.Id }, report.Reused.ToHashSet());
    }

    [Fact]
    public void Search_MatchesTitleUserUrlTag_CaseInsensitive()
    {
        var entries = new[]
        {
            E("GitHub", user: "alice"),
            E("Bank", url: "https://mybank.example"),
            E("Mail", tags: ["Work"]),
            E("Trashed GitHub", trashed: true),
        };
        Assert.Single(EntrySearch.Filter(entries, "github"));
        Assert.Single(EntrySearch.Filter(entries, "ALICE"));
        Assert.Single(EntrySearch.Filter(entries, "mybank"));
        Assert.Single(EntrySearch.Filter(entries, "work"));
        Assert.Single(EntrySearch.Filter(entries, "", tag: "work"));
        Assert.Empty(EntrySearch.Filter(entries, "github bank"));
        Assert.Equal(3, EntrySearch.Filter(entries, "").Count);
    }

    [Fact]
    public void Search_PutsFavoritesFirst()
    {
        var entries = new[] { E("a"), E("b"), E("z", favorite: true) };
        Assert.Equal(["z", "a", "b"], EntrySearch.Filter(entries, null).Select(e => e.Data.Title));
    }

    [Fact]
    public void Search_1000Entries_Under100ms()
    {
        var entries = Enumerable.Range(0, 1000)
            .Select(i => E($"Site {i}", user: $"user{i}@example.com", url: $"https://site{i}.example", tags: [$"tag{i % 10}"]))
            .ToList();
        // 並び替え（カルチャ依存の比較）の初期化はアンロック直後の一覧表示で 1 度だけ起きるので、計測前に済ませる
        EntrySearch.Filter(entries, "");

        var times = new List<double>();
        IReadOnlyList<VaultEntry> result = [];
        foreach (var query in new[] { "site 99", "user5", "tag3", "example", "s" })
        {
            var sw = Stopwatch.StartNew();
            result = EntrySearch.Filter(entries, query);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        Assert.NotEmpty(result);
        var median = times.Order().ElementAt(times.Count / 2);
        TestContext.Current.TestOutputHelper?.WriteLine($"search times (ms): {string.Join(", ", times.Select(t => t.ToString("0.00")))}");
        Assert.True(median < 100, $"median {median:0.0} ms / all: {string.Join(", ", times.Select(t => t.ToString("0.0")))}");
    }
}

public class CsvTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parser_HandlesQuotesCommasAndNewlines()
    {
        var rows = Csv.Parse("a,b,c\r\n\"x,1\",\"he said \"\"hi\"\"\",\"line1\nline2\"\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["x,1", "he said \"hi\"", "line1\nline2"], rows[1]);
    }

    [Fact]
    public void Imports_Chrome()
    {
        var r = CsvImporter.Import("name,url,username,password,note\nGitHub,https://github.com/login,alice,pw1,memo\n", Now);
        Assert.Equal(CsvFormat.Browser, r.Format);
        var e = Assert.Single(r.Entries);
        Assert.Equal(("GitHub", "alice", "pw1", "memo"), (e.Title, e.Username, e.Password, e.Notes));
    }

    [Fact]
    public void Imports_Firefox_UsingHostAsTitle()
    {
        var r = CsvImporter.Import(
            "\"url\",\"username\",\"password\",\"httpRealm\",\"formActionOrigin\",\"guid\",\"timeCreated\",\"timeLastUsed\",\"timePasswordChanged\"\n" +
            "\"https://example.com\",\"bob\",\"pw2\",,\"https://example.com\",\"{1}\",\"1700000000000\",\"1700000000000\",\"1700000000000\"\n", Now);
        Assert.Equal(CsvFormat.Browser, r.Format);
        var e = Assert.Single(r.Entries);
        Assert.Equal("example.com", e.Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), e.CreatedAt);
    }

    [Fact]
    public void Imports_Bitwarden_SkippingNonLogins()
    {
        var r = CsvImporter.Import(
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
            "Work,1,login,Mail,n,,0,https://mail.example,carol,pw3,\n" +
            ",,note,Secret note,text,,0,,,,\n", Now);
        Assert.Equal(CsvFormat.Bitwarden, r.Format);
        var e = Assert.Single(r.Entries);
        Assert.Equal(("Mail", "carol", "pw3", "https://mail.example"), (e.Title, e.Username, e.Password, e.Url));
        Assert.Equal(["Work"], e.Tags);
        Assert.True(e.Favorite);
        Assert.Equal(1, r.SkippedRows);
    }

    [Fact]
    public void Imports_KeePassXC()
    {
        var r = CsvImporter.Import(
            "\"Group\",\"Title\",\"Username\",\"Password\",\"URL\",\"Notes\",\"TOTP\",\"Icon\",\"Last Modified\",\"Created\"\n" +
            "\"Root/仕事\",\"VPN\",\"dave\",\"pw4\",\"https://vpn.example\",\"\",\"\",\"0\",\"2024-01-01T00:00:00Z\",\"2023-05-01T10:00:00Z\"\n", Now);
        Assert.Equal(CsvFormat.KeePassXC, r.Format);
        var e = Assert.Single(r.Entries);
        Assert.Equal(["仕事"], e.Tags);
        Assert.Equal(new DateTimeOffset(2023, 5, 1, 10, 0, 0, TimeSpan.Zero), e.CreatedAt);
    }

    [Fact]
    public void UnknownFormat_ImportsNothing()
    {
        var r = CsvImporter.Import("foo,bar\n1,2\n", Now);
        Assert.Equal(CsvFormat.Unknown, r.Format);
        Assert.Empty(r.Entries);
    }

    [Fact]
    public void Export_ThenImport_RoundTrips()
    {
        var original = new VaultEntry(Guid.NewGuid(), 1, Now, new EntryData
        {
            Title = "A, \"quoted\"", Username = "u", Password = "p,w\"d", Url = "https://a.example",
            Notes = "line1\nline2", Tags = ["x", "y"], CreatedAt = Now, Favorite = true,
        });
        var trashed = original with { Id = Guid.NewGuid(), Data = new EntryData { Title = "gone", TrashedAt = Now } };

        var csv = CsvExporter.Export([original, trashed]);
        var r = CsvImporter.Import(csv, Now);

        Assert.Equal(CsvFormat.PwVault, r.Format);
        var e = Assert.Single(r.Entries);
        Assert.Equal(original.Data.Title, e.Title);
        Assert.Equal(original.Data.Password, e.Password);
        Assert.Equal(original.Data.Notes, e.Notes);
        Assert.Equal(original.Data.Tags, e.Tags);
        Assert.Equal(Now, e.CreatedAt);
        Assert.True(e.Favorite);
    }
}

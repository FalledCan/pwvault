using System.Text;
using PwVault.Core.Bridge;
using PwVault.Core.Interop;
using PwVault.Core.Otp;
using PwVault.Core.Tools;

namespace PwVault.Core.Tests;

public class TotpTests
{
    // RFC 6238 付録 B のテストベクタ（8 桁、30 秒）
    [Theory]
    [InlineData(59L, OtpAlgorithm.Sha1, "94287082")]
    [InlineData(59L, OtpAlgorithm.Sha256, "46119246")]
    [InlineData(59L, OtpAlgorithm.Sha512, "90693936")]
    [InlineData(1111111109L, OtpAlgorithm.Sha1, "07081804")]
    [InlineData(1111111109L, OtpAlgorithm.Sha256, "68084774")]
    [InlineData(1111111109L, OtpAlgorithm.Sha512, "25091201")]
    [InlineData(1111111111L, OtpAlgorithm.Sha1, "14050471")]
    [InlineData(1234567890L, OtpAlgorithm.Sha256, "91819424")]
    [InlineData(2000000000L, OtpAlgorithm.Sha512, "38618901")]
    [InlineData(20000000000L, OtpAlgorithm.Sha1, "65353130")]
    [InlineData(20000000000L, OtpAlgorithm.Sha256, "77737706")]
    [InlineData(20000000000L, OtpAlgorithm.Sha512, "47863826")]
    public void Rfc6238_Vectors(long unixSeconds, OtpAlgorithm algorithm, string expected)
    {
        var seed = algorithm switch
        {
            OtpAlgorithm.Sha256 => "12345678901234567890123456789012",
            OtpAlgorithm.Sha512 => "1234567890123456789012345678901234567890123456789012345678901234",
            _ => "12345678901234567890",
        };
        var key = new TotpKey(Encoding.ASCII.GetBytes(seed), algorithm, digits: 8);
        Assert.Equal(expected, key.Generate(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public void SixDigits_KeepsLeadingZeros_AndRemainingSeconds()
    {
        var key = new TotpKey(Encoding.ASCII.GetBytes("12345678901234567890"));
        // 8 桁の 07081804 の下 6 桁
        Assert.Equal("081804", key.Generate(DateTimeOffset.FromUnixTimeSeconds(1111111109)));
        Assert.Equal(30, key.SecondsRemaining(DateTimeOffset.FromUnixTimeSeconds(1111111110)));
        Assert.Equal(1, key.SecondsRemaining(DateTimeOffset.FromUnixTimeSeconds(1111111109)));
    }

    [Fact]
    public void Base32_RoundTrips_AndIgnoresSpacesAndCase()
    {
        var bytes = Encoding.ASCII.GetBytes("12345678901234567890");
        var text = Base32.Encode(bytes);
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", text);
        Assert.True(Base32.TryDecode("gezd gnbv-gy3t qojq gezd gnbv gy3t qojq", out var decoded));
        Assert.Equal(bytes, decoded);
        Assert.False(Base32.TryDecode("GEZ1", out _)); // 1 は Base32 に無い
    }

    [Fact]
    public void Parse_PlainKey()
    {
        Assert.True(TotpKey.TryParse(" JBSW Y3DP EHPK 3PXP ", out var key, out _));
        Assert.Equal(Encoding.ASCII.GetBytes("Hello!Þ­¾ï").Length, key!.Secret.Length);
        Assert.Equal(OtpAlgorithm.Sha1, key.Algorithm);
        Assert.Equal(6, key.Digits);
        Assert.Equal(30, key.Period);
    }

    [Fact]
    public void Parse_OtpauthUri_WithAllParameters()
    {
        Assert.True(TotpKey.TryParse(
            "otpauth://totp/ACME%20Co:john.doe@email.com?secret=HXDMVJECJJWSRB3HWIZR4IFUGFTMXBOZ&issuer=ACME%20Co&algorithm=SHA256&digits=8&period=60",
            out var key, out var error), error);
        Assert.Equal("ACME Co", key!.Issuer);
        Assert.Equal("john.doe@email.com", key.Account);
        Assert.Equal(OtpAlgorithm.Sha256, key.Algorithm);
        Assert.Equal(8, key.Digits);
        Assert.Equal(60, key.Period);

        // 保存用の形から読み直しても同じ
        Assert.True(TotpKey.TryParse(key.ToUri(), out var again, out _));
        Assert.True(key.SameSecret(again!));
        Assert.Equal("ACME Co", again!.Issuer);
        Assert.Equal("john.doe@email.com", again.Account);
    }

    [Fact]
    public void Parse_LabelOnlyIssuer_AndDefaults()
    {
        Assert.True(TotpKey.TryParse("otpauth://totp/GitHub:alice?secret=JBSWY3DPEHPK3PXP", out var key, out _));
        Assert.Equal("GitHub", key!.Issuer);
        Assert.Equal("alice", key.Account);
        Assert.Equal("otpauth://totp/GitHub:alice?secret=JBSWY3DPEHPK3PXP&issuer=GitHub", key.ToUri());
    }

    [Theory]
    [InlineData("", "空")]
    [InlineData("otpauth://hotp/x?secret=JBSWY3DPEHPK3PXP&counter=1", "HOTP")]
    [InlineData("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&algorithm=MD5", "MD5")]
    [InlineData("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP&digits=4", "桁")]
    [InlineData("otpauth://totp/x?issuer=a", "secret")]
    [InlineData("otpauth-migration://offline?data=AA", "Google Authenticator")]
    [InlineData("steam://ABCDEF", "対応していない")]
    [InlineData("これはキーではない", "使えない文字")]
    [InlineData("ABCD", "短すぎる")]
    public void Parse_RejectsInvalid(string input, string reason)
    {
        Assert.False(TotpKey.TryParse(input, out var key, out var error));
        Assert.Null(key);
        Assert.Contains(reason, error);
    }

    [Fact]
    public void Normalize_FillsNamesFromEntry()
    {
        var uri = TotpKey.Normalize("JBSWY3DPEHPK3PXP", "Example", "bob");
        Assert.Equal("otpauth://totp/Example:bob?secret=JBSWY3DPEHPK3PXP&issuer=Example", uri);
        Assert.Equal("", TotpKey.Normalize("steam://XYZ", "a", "b"));
        Assert.Equal("", TotpKey.Normalize("", "a", "b"));
    }

    // ------------------------------------------------------------------ Google Authenticator の移行用 QR

    /// <summary>テスト用に MigrationPayload を組み立てる（Protocol Buffers の varint と長さ付き項目）。</summary>
    internal static string MigrationUri(IEnumerable<(byte[] Secret, string Name, string Issuer, int Algorithm, int Digits, int Type)> otps,
        int batchSize = 1, int batchIndex = 0, int batchId = 12345)
    {
        var payload = new List<byte>();
        foreach (var o in otps)
        {
            var p = new List<byte>();
            Bytes(p, 1, o.Secret);
            Bytes(p, 2, Encoding.UTF8.GetBytes(o.Name));
            Bytes(p, 3, Encoding.UTF8.GetBytes(o.Issuer));
            Varint(p, 4, (ulong)o.Algorithm);
            Varint(p, 5, (ulong)o.Digits);
            Varint(p, 6, (ulong)o.Type);
            Bytes(payload, 1, [.. p]);
        }
        Varint(payload, 2, 1);
        Varint(payload, 3, (ulong)batchSize);
        Varint(payload, 4, (ulong)batchIndex);
        Varint(payload, 5, (ulong)batchId);
        return "otpauth-migration://offline?data=" + Uri.EscapeDataString(Convert.ToBase64String([.. payload]));

        static void Raw(List<byte> b, ulong v)
        {
            do
            {
                var x = (byte)(v & 0x7f);
                v >>= 7;
                b.Add(v != 0 ? (byte)(x | 0x80) : x);
            } while (v != 0);
        }
        static void Varint(List<byte> b, int field, ulong v) { Raw(b, (ulong)(field << 3)); Raw(b, v); }
        static void Bytes(List<byte> b, int field, byte[] v) { Raw(b, (ulong)(field << 3 | 2)); Raw(b, (ulong)v.Length); b.AddRange(v); }
    }

    internal static readonly byte[] SecretA = Encoding.ASCII.GetBytes("12345678901234567890");
    internal static readonly byte[] SecretB = Encoding.ASCII.GetBytes("abcdefghijabcdefghij");

    [Fact]
    public void Migration_ReadsAccounts_AndSkipsHotp()
    {
        var uri = MigrationUri(
        [
            (SecretA, "alice@example.com", "Example", 1, 1, 2),
            (SecretB, "GitHub:bob", "", 2, 2, 2),     // 名前に発行元が入っている・SHA256・8 桁
            (SecretA, "counter", "Old", 1, 1, 1),      // HOTP は取り込めない
            (SecretA, "md5", "Old", 4, 1, 2),          // MD5 も取り込めない
        ], batchSize: 2, batchIndex: 1);

        Assert.True(GoogleAuthMigration.TryParse(uri, out var batch, out var error), error);
        Assert.Equal(2, batch!.Keys.Count);
        Assert.Equal(2, batch.Unsupported);
        Assert.Equal(2, batch.BatchSize);
        Assert.Equal(1, batch.BatchIndex);
        Assert.Equal(12345, batch.BatchId);

        var a = batch.Keys[0];
        Assert.Equal(("Example", "alice@example.com"), (a.Issuer, a.Account));
        Assert.Equal(SecretA, a.Secret);
        Assert.Equal((OtpAlgorithm.Sha1, 6, 30), (a.Algorithm, a.Digits, a.Period));
        // Google Authenticator と同じ番号になる
        Assert.Equal("081804", a.Generate(DateTimeOffset.FromUnixTimeSeconds(1111111109)));

        var b = batch.Keys[1];
        Assert.Equal(("GitHub", "bob"), (b.Issuer, b.Account));
        Assert.Equal((OtpAlgorithm.Sha256, 8), (b.Algorithm, b.Digits));
    }

    [Theory]
    [InlineData("otpauth://totp/x?secret=JBSWY3DPEHPK3PXP")]
    [InlineData("otpauth-migration://offline")]
    [InlineData("otpauth-migration://offline?data=%%%")]
    [InlineData("otpauth-migration://offline?data=CgQK")] // 途中で切れている
    [InlineData("otpauth-migration://offline?data=AAAA")] // 項目番号 0
    public void Migration_RejectsBrokenData(string uri)
    {
        Assert.False(GoogleAuthMigration.TryParse(uri, out var batch, out var error));
        Assert.Null(batch);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("", "a:b")]         // 発行元が無く、アカウントに「:」
    [InlineData("Corp:Tokyo", "alice")] // 発行元に「:」
    [InlineData("Corp:Tokyo", "a:b")]
    [InlineData("", "")]
    [InlineData("Example", "")]
    public void ToUri_RoundTripsNames_EvenWithColons(string issuer, string account)
    {
        var key = new TotpKey(SecretA, issuer: issuer, account: account);
        var again = TotpKey.FromStored(key.ToUri())!;
        Assert.True(again.SameSecret(key));
        Assert.Equal(issuer, again.Issuer);
        Assert.Equal(account, again.Account);
    }

    /// <summary>敵対検証: でたらめな移行用データ・otpauth を大量に与えても、例外で落ちず、読めたものは保存形で往復できる。</summary>
    [Fact]
    public void Fuzz_RandomInput_NeverThrows()
    {
        var rng = new Random(20260930);
        for (var i = 0; i < 20000; i++)
        {
            var bytes = new byte[rng.Next(0, 160)];
            rng.NextBytes(bytes);
            if (i % 2 == 0 && bytes.Length > 3) { bytes[0] = 0x0a; bytes[1] = (byte)(bytes.Length - 2); bytes[2] = 0x0a; bytes[3] = 5; }
            GoogleAuthMigration.TryParse("otpauth-migration://offline?data=" + Uri.EscapeDataString(Convert.ToBase64String(bytes)), out _, out _);
        }

        string[] parts = ["otpauth://totp/", "otpauth://hotp/", "otpauth:totp", "?", "&", "=", "%", "%zz", ":", "secret=", "JBSWY3DPEHPK3PXP",
            "digits=", "period=", "algorithm=", "issuer=", "#", "@", "/", "\\", "\0", "é", "😀", " ", "+", "-", "99999999999999", "0", "８"];
        for (var i = 0; i < 20000; i++)
        {
            var text = string.Concat(Enumerable.Range(0, rng.Next(1, 9)).Select(_ => parts[rng.Next(parts.Length)]));
            if (!TotpKey.TryParse(text, out var key, out _)) continue;
            key!.Generate(DateTimeOffset.UtcNow);
            var again = TotpKey.FromStored(key.ToUri());
            Assert.True(again is not null && again.SameSecret(key) && again.Issuer == key.Issuer && again.Account == key.Account, text);
        }
    }

    [Fact]
    public void Migration_AcceptsUnescapedBase64()
    {
        // QR の読み取りアプリによっては %2B などを戻した形で渡してくる
        var uri = Uri.UnescapeDataString(MigrationUri([(SecretA, "a", "Example", 1, 1, 2)]));
        Assert.True(GoogleAuthMigration.TryParse(uri, out var batch, out _));
        Assert.Single(batch!.Keys);
    }

    // ------------------------------------------------------------------ 既存のエントリとの対応付け

    private static VaultEntry Entry(string title, string user, string url = "") =>
        new(Guid.NewGuid(), 1, DateTimeOffset.UtcNow, new EntryData { Title = title, Username = user, Url = url });

    [Fact]
    public void Matcher_PrefersIssuerAndAccount()
    {
        var gh1 = Entry("GitHub", "alice");
        var gh2 = Entry("GitHub", "bob");
        var mail = Entry("メール", "bob", "https://mail.example");
        var google = Entry("仕事用", "bob@gmail.com", "https://accounts.google.com");
        var entries = new[] { gh1, gh2, mail, google };

        Assert.Same(gh2, TotpMatcher.Suggest(entries, new TotpKey(SecretA, issuer: "GitHub", account: "bob")));
        Assert.Same(gh1, TotpMatcher.Suggest(entries, new TotpKey(SecretA, issuer: "github", account: "alice")));
        Assert.Same(google, TotpMatcher.Suggest(entries, new TotpKey(SecretA, issuer: "Google", account: "bob@gmail.com")));
        // 発行元が合わなければ、アカウント名だけでは選ばない
        Assert.Null(TotpMatcher.Suggest(entries, new TotpKey(SecretA, issuer: "Amazon", account: "bob")));
        // 発行元が無ければアカウント名で
        Assert.Same(mail, TotpMatcher.Suggest([mail], new TotpKey(SecretA, account: "bob")));
    }

    // ------------------------------------------------------------------ 保存・自動タイプ・CSV・ブラウザ連携

    private static string SampleUri => new TotpKey(SecretA, issuer: "Example", account: "alice").ToUri();

    [Fact]
    public void StoredInsideCiphertext_AndSurvivesReload()
    {
        using var dir = new TempDir();
        var path = dir.File("v.pwv");
        Guid id;
        using (var v = Vault.Create(path, "correct horse battery staple", TestKdf.Fast()))
        {
            id = v.AddEntry(new EntryData { Title = "Example", Totp = SampleUri, AutoTypeTotp = true });
            v.Save(1);
        }
        Assert.DoesNotContain("otpauth", File.ReadAllText(path));
        Assert.DoesNotContain(Base32.Encode(SecretA), File.ReadAllText(path));

        using var reopened = Vault.Open(path, "correct horse battery staple");
        var data = reopened.GetEntry(id)!.Data;
        Assert.Equal(SampleUri, data.Totp);
        Assert.True(data.AutoTypeTotp);
        Assert.Equal(SampleUri, data.Clone().Totp);
    }

    [Fact]
    public void AutoType_TypesCodeAfterPassword_WhenEnabled()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var data = new EntryData { Username = "alice", Password = "pw", Totp = SampleUri };
        Assert.Equal(3, AutoTypeMatcher.Sequence(data, now).Count); // 設定しなければ打たない

        data.AutoTypeTotp = true;
        var actions = AutoTypeMatcher.Sequence(data, now);
        Assert.Equal(
            [new AutoTypeAction.Text("alice"), new AutoTypeAction.Tab(), new AutoTypeAction.Text("pw"), new AutoTypeAction.Tab(), new AutoTypeAction.Text("081804")],
            actions);

        // コードだけ（ID・パスワードが無い）なら Tab は付けない
        Assert.Equal([new AutoTypeAction.Text("081804")],
            AutoTypeMatcher.Sequence(new EntryData { Totp = SampleUri, AutoTypeTotp = true }, now));

        // ID はあるがパスワードが空: ID の後の Tab だけで番号の欄に進む（Tab を 2 回押して欄を飛ばさない）
        Assert.Equal([new AutoTypeAction.Text("alice"), new AutoTypeAction.Tab(), new AutoTypeAction.Text("081804")],
            AutoTypeMatcher.Sequence(new EntryData { Username = "alice", Totp = SampleUri, AutoTypeTotp = true }, now));
    }

    [Fact]
    public void Csv_ImportsTotpColumns_AndExportsThem()
    {
        var now = DateTimeOffset.UtcNow;
        var bitwarden = CsvImporter.Import(
            "folder,favorite,type,name,notes,fields,reprompt,login_uri,login_username,login_password,login_totp\n" +
            ",,login,Example,,,,https://example.com,alice,pw1,JBSWY3DPEHPK3PXP\n" +
            ",,login,Steam,,,,https://steam.example,bob,pw2,steam://ABCDEF\n", now);
        Assert.Equal("otpauth://totp/Example:alice?secret=JBSWY3DPEHPK3PXP&issuer=Example", bitwarden.Entries[0].Totp);
        Assert.Equal("", bitwarden.Entries[1].Totp);

        var keepass = CsvImporter.Import(
            "\"Group\",\"Title\",\"Username\",\"Password\",\"URL\",\"Notes\",\"TOTP\",\"Icon\",\"Last Modified\",\"Created\"\n" +
            "\"Root\",\"GitHub\",\"bob\",\"pw\",\"https://github.com\",\"\",\"otpauth://totp/GitHub:bob?secret=JBSWY3DPEHPK3PXP&period=30&digits=6&issuer=GitHub\",\"0\",\"\",\"\"\n", now);
        Assert.Equal("otpauth://totp/GitHub:bob?secret=JBSWY3DPEHPK3PXP&issuer=GitHub", keepass.Entries[0].Totp);

        var exported = CsvExporter.Export([new VaultEntry(Guid.NewGuid(), 1, now, new EntryData { Title = "Example", Username = "alice", Password = "pw", Totp = SampleUri })]);
        var back = CsvImporter.Import(exported, now);
        Assert.Equal(CsvFormat.PwVault, back.Format);
        Assert.Equal(SampleUri, back.Entries[0].Totp);
    }

    [Fact]
    public void Bridge_ListsTotpFlag_AndGivesOnlyTheCode_ForMatchingSite()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111109);
        var site = new VaultEntry(Guid.NewGuid(), 1, now, new EntryData { Title = "Example", Username = "alice", Url = "https://example.com", Totp = SampleUri });
        var plain = new VaultEntry(Guid.NewGuid(), 1, now, new EntryData { Title = "Plain", Password = "pw", Url = "https://example.com" });
        var other = new VaultEntry(Guid.NewGuid(), 1, now, new EntryData { Title = "Other", Url = "https://other.test", Totp = SampleUri });
        VaultEntry[] entries = [site, plain, other];

        var list = BridgeHandler.Handle(new BridgeRequest { Type = "list", Url = "https://example.com/login" }, entries);
        Assert.True(list.Entries!.Single(e => e.Title == "Example").Totp);
        Assert.Null(list.Entries!.Single(e => e.Title == "Plain").Totp);
        var json = Encoding.UTF8.GetString(BridgeMessage.Serialize(list));
        Assert.DoesNotContain("otpauth", json);

        var ok = BridgeHandler.Handle(new BridgeRequest { Type = "otp", Url = "https://example.com/2fa", Id = site.Id.ToString() }, entries, now: now);
        Assert.True(ok.Ok);
        Assert.Equal("081804", ok.Code);
        Assert.Null(ok.Password);
        Assert.DoesNotContain("otpauth", Encoding.UTF8.GetString(BridgeMessage.Serialize(ok)));

        // 別のサイトのエントリ、ワンタイムパスワードの無いエントリ、ロック中は渡さない
        Assert.Equal("no_match", BridgeHandler.Handle(new BridgeRequest { Type = "otp", Url = "https://example.com/", Id = other.Id.ToString() }, entries).Error);
        Assert.Equal("no_match", BridgeHandler.Handle(new BridgeRequest { Type = "otp", Url = "https://example.com/", Id = plain.Id.ToString() }, entries).Error);
        Assert.Equal("locked", BridgeHandler.Handle(new BridgeRequest { Type = "otp", Url = "https://example.com/", Id = site.Id.ToString() }, null).Error);
        Assert.Equal("bad_request", BridgeHandler.Handle(new BridgeRequest { Type = "otp", Url = "https://example.com/", Id = "x" }, entries).Error);
    }
}

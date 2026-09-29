using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PwVault.Core.Otp;

public enum OtpAlgorithm { Sha1, Sha256, Sha512 }

/// <summary>
/// 時刻から作るワンタイムパスワード（TOTP、RFC 6238）の「元」。Google Authenticator などと同じ方式で、
/// 同じ秘密のキーと時刻からは、どの端末でも同じ番号になる。通信はしない。
/// エントリには <see cref="ToUri"/> の形（otpauth://totp/...）で、暗号文の中に保存する。
/// </summary>
public sealed class TotpKey
{
    public const int MinSecretBytes = 5;
    public const int MaxSecretBytes = 256;

    public TotpKey(byte[] secret, OtpAlgorithm algorithm = OtpAlgorithm.Sha1, int digits = 6, int period = 30,
        string issuer = "", string account = "")
    {
        if (secret.Length is < MinSecretBytes or > MaxSecretBytes) throw new ArgumentException("キーの長さが不正です。", nameof(secret));
        if (digits is < 6 or > 8) throw new ArgumentOutOfRangeException(nameof(digits));
        if (period is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(period));
        Secret = secret;
        Algorithm = algorithm;
        Digits = digits;
        Period = period;
        Issuer = issuer;
        Account = account;
    }

    public byte[] Secret { get; }
    public OtpAlgorithm Algorithm { get; }
    public int Digits { get; }
    public int Period { get; }

    /// <summary>発行元（サイト・サービスの名前）。</summary>
    public string Issuer { get; }

    /// <summary>アカウント名（メールアドレスなど）。</summary>
    public string Account { get; }

    /// <summary>秘密のキーと計算方法が同じか（名前の違いは問わない）。</summary>
    public bool SameSecret(TotpKey other) =>
        Algorithm == other.Algorithm && Digits == other.Digits && Period == other.Period &&
        CryptographicOperations.FixedTimeEquals(Secret, other.Secret);

    public TotpKey WithNames(string issuer, string account) => new(Secret, Algorithm, Digits, Period, issuer, account);

    /// <summary>その時刻のコード（先頭の 0 も含めた桁数の文字列）。</summary>
    public string Generate(DateTimeOffset now) => Generate(Step(now));

    internal string Generate(long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        byte[] mac = Algorithm switch
        {
            OtpAlgorithm.Sha256 => HMACSHA256.HashData(Secret, counter),
            OtpAlgorithm.Sha512 => HMACSHA512.HashData(Secret, counter),
            _ => HMACSHA1.HashData(Secret, counter),
        };
        var offset = mac[^1] & 0x0f;
        var binary = BinaryPrimitives.ReadInt32BigEndian(mac.AsSpan(offset)) & 0x7fffffff;
        var modulo = (int)Math.Pow(10, Digits);
        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
    }

    private long Step(DateTimeOffset now) => Math.Max(0, now.ToUnixTimeSeconds()) / Period;

    /// <summary>今のコードが変わるまでの秒数（1〜Period）。</summary>
    public int SecondsRemaining(DateTimeOffset now) => Period - (int)(Math.Max(0, now.ToUnixTimeSeconds()) % Period);

    /// <summary>保存用の形（otpauth://totp/発行元:アカウント?secret=...&amp;issuer=...）。既定値の項目は省く。</summary>
    public string ToUri()
    {
        var label = Issuer.Length > 0 ? Uri.EscapeDataString(Issuer) + ":" + Uri.EscapeDataString(Account) : Uri.EscapeDataString(Account);
        var query = new StringBuilder("?secret=").Append(Base32.Encode(Secret));
        if (Issuer.Length > 0) query.Append("&issuer=").Append(Uri.EscapeDataString(Issuer));
        if (Algorithm != OtpAlgorithm.Sha1) query.Append("&algorithm=").Append(Algorithm.ToString().ToUpperInvariant());
        if (Digits != 6) query.Append("&digits=").Append(Digits.ToString(CultureInfo.InvariantCulture));
        if (Period != 30) query.Append("&period=").Append(Period.ToString(CultureInfo.InvariantCulture));
        return "otpauth://totp/" + label + query;
    }

    /// <summary>
    /// 利用者が貼り付けた文字を読む。受け付けるのは「otpauth://totp/...」か、サイトの設定画面に出るキーの文字列
    /// （Base32。空白・ハイフン・大文字小文字は問わない）。読めなければ error に理由を入れて false。
    /// </summary>
    public static bool TryParse(string? input, out TotpKey? key, out string? error)
    {
        key = null;
        error = null;
        var text = (input ?? "").Trim();
        if (text.Length == 0) { error = "キーが空です。"; return false; }
        if (text.Length > 4096) { error = "長すぎます。"; return false; }

        if (text.StartsWith("otpauth-migration:", StringComparison.OrdinalIgnoreCase))
        {
            error = "Google Authenticator の移行用のデータです。「取込/書出」の「Google Authenticator から移す」で取り込んでください。";
            return false;
        }
        if (text.StartsWith("otpauth:", StringComparison.OrdinalIgnoreCase))
            return TryParseUri(text, out key, out error);
        if (text.Contains("://", StringComparison.Ordinal))
        {
            error = "対応していない形式です（otpauth:// か、キーの文字列を入れてください）。";
            return false;
        }

        if (!Base32.TryDecode(text, out var secret))
        {
            error = "キーに使えない文字が含まれています（A〜Z と 2〜7 の文字列です）。";
            return false;
        }
        if (secret.Length is < MinSecretBytes or > MaxSecretBytes)
        {
            error = "キーが短すぎるか長すぎます。サイトに表示されたキーを全部入れてください。";
            return false;
        }
        key = new TotpKey(secret);
        return true;
    }

    private static bool TryParseUri(string text, out TotpKey? key, out string? error)
    {
        key = null;
        error = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            error = "otpauth:// の形式が正しくありません。";
            return false;
        }
        var type = uri.Host.ToLowerInvariant();
        if (type == "hotp")
        {
            error = "回数で変わる方式（HOTP）には対応していません。時刻で変わる方式（TOTP）だけに対応しています。";
            return false;
        }
        if (type != "totp")
        {
            error = "otpauth:// の形式が正しくありません。";
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("secret", out var secretText) || !Base32.TryDecode(secretText, out var secret) ||
            secret.Length is < MinSecretBytes or > MaxSecretBytes)
        {
            error = "キー（secret）が無いか、正しくありません。";
            return false;
        }

        var algorithm = OtpAlgorithm.Sha1;
        if (query.TryGetValue("algorithm", out var alg))
        {
            switch (alg.ToUpperInvariant().Replace("-", ""))
            {
                case "SHA1": break;
                case "SHA256": algorithm = OtpAlgorithm.Sha256; break;
                case "SHA512": algorithm = OtpAlgorithm.Sha512; break;
                default: error = $"計算方式（{alg}）には対応していません。"; return false;
            }
        }

        var digits = 6;
        if (query.TryGetValue("digits", out var d) && (!int.TryParse(d, NumberStyles.None, CultureInfo.InvariantCulture, out digits) || digits is < 6 or > 8))
        {
            error = "桁数は 6〜8 桁に対応しています。";
            return false;
        }

        var period = 30;
        if (query.TryGetValue("period", out var p) && (!int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out period) || period is < 1 or > 3600))
        {
            error = "更新間隔（period）が正しくありません。";
            return false;
        }

        // ラベルは「発行元:アカウント」か「アカウント」。issuer の指定があればそちらを優先する
        var label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var colon = label.IndexOf(':');
        var issuer = colon >= 0 ? label[..colon].Trim() : "";
        var account = (colon >= 0 ? label[(colon + 1)..] : label).Trim();
        if (query.TryGetValue("issuer", out var i) && i.Trim().Length > 0) issuer = i.Trim();

        key = new TotpKey(secret, algorithm, digits, period, issuer, account);
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = Uri.UnescapeDataString(eq >= 0 ? part[..eq] : part);
            var value = eq >= 0 ? Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')) : "";
            result.TryAdd(name, value);
        }
        return result;
    }

    /// <summary>
    /// 取り込んだ文字（otpauth:// かキーの文字列）を保存用の形にする。名前が無ければエントリのタイトル・ユーザー ID を使う。
    /// 読めなければ（Steam の形式など）空文字。
    /// </summary>
    public static string Normalize(string? raw, string defaultIssuer, string defaultAccount)
    {
        if (string.IsNullOrWhiteSpace(raw) || !TryParse(raw, out var key, out _)) return "";
        return key!.WithNames(key.Issuer.Length > 0 ? key.Issuer : defaultIssuer.Trim(),
            key.Account.Length > 0 ? key.Account : defaultAccount.Trim()).ToUri();
    }

    /// <summary>エントリに保存した文字列を読む（空・読めなければ null）。</summary>
    public static TotpKey? FromStored(string? stored) =>
        !string.IsNullOrWhiteSpace(stored) && TryParse(stored, out var key, out _) ? key : null;
}

/// <summary>Base32（RFC 4648）。TOTP のキーの表し方。</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    /// <summary>空白・ハイフン・末尾の = を無視し、大文字・小文字を区別せずに読む。</summary>
    public static bool TryDecode(string text, out byte[] data)
    {
        data = [];
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var raw in text.TrimEnd('=', ' '))
        {
            if (raw is ' ' or '-' or '\t') continue;
            var index = Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0) return false;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        data = [.. output];
        return true;
    }
}

namespace PwVault.Core.Tools;

public enum StrengthLevel
{
    VeryWeak = 0,
    Weak = 1,
    Fair = 2,
    Strong = 3,
    VeryStrong = 4,
}

public sealed record StrengthResult(StrengthLevel Level, double EntropyBits, IReadOnlyList<string> Warnings)
{
    public string Label => Level switch
    {
        StrengthLevel.VeryWeak => "非常に弱い",
        StrengthLevel.Weak => "弱い",
        StrengthLevel.Fair => "普通",
        StrengthLevel.Strong => "強い",
        _ => "非常に強い",
    };

    public bool IsWeak => Level <= StrengthLevel.Fair;
}

/// <summary>
/// 端末内で完結する簡易的なパスワード強度推定（SR-09, FR-14）。
/// 文字種から見積もったエントロピーを、よくある弱いパターン（繰り返し・連番・キーボード配列・頻出語）で減点する。
/// </summary>
public static class PasswordStrength
{
    public const int MasterPasswordMinLength = 12;

    private static readonly string[] CommonWords =
    [
        "password", "passw0rd", "qwerty", "letmein", "welcome", "admin", "iloveyou", "monkey", "dragon",
        "master", "login", "princess", "sunshine", "football", "baseball", "superman", "abc123", "trustno1",
        "hello", "secret", "shadow", "michael", "pokemon", "starwars", "whatever", "freedom", "123456",
        "654321", "111111", "000000", "asdfgh", "zxcvbn", "1qaz2wsx", "qazwsx", "passwort", "pass",
    ];

    private static readonly string[] Sequences =
    [
        "abcdefghijklmnopqrstuvwxyz", "0123456789", "qwertyuiop", "asdfghjkl", "zxcvbnm", "1qaz2wsx3edc",
    ];

    public static StrengthResult Evaluate(string password)
    {
        var warnings = new List<string>();
        if (password.Length == 0)
            return new StrengthResult(StrengthLevel.VeryWeak, 0, ["パスワードが空です。"]);

        var pool = 0;
        if (password.Any(char.IsLower)) pool += 26;
        if (password.Any(char.IsUpper)) pool += 26;
        if (password.Any(char.IsDigit)) pool += 10;
        if (password.Any(c => c < 128 && !char.IsLetterOrDigit(c))) pool += 33;
        if (password.Any(c => c >= 128)) pool += 100;

        // 実効長: 同じ文字の連続・連番・キーボード配列の並びは 1 文字あたりの情報量が少ないとみなす
        var effective = EffectiveLength(password, warnings);
        var bits = effective * Math.Log2(Math.Max(pool, 2));

        var lower = password.ToLowerInvariant();
        if (CommonWords.Any(w => lower.Contains(w)))
        {
            warnings.Add("よく使われる単語やパターンが含まれています。");
            bits = Math.Min(bits, 20 + Math.Max(0, lower.Length - LongestCommonWord(lower)) * 3);
        }

        if (password.Length < MasterPasswordMinLength)
            warnings.Add($"{MasterPasswordMinLength} 文字以上にすると安全性が上がります。");
        if (pool <= 26)
            warnings.Add("大文字・数字・記号を混ぜるか、単語を 4 つ以上つなげると強くなります。");

        var level = bits switch
        {
            < 28 => StrengthLevel.VeryWeak,
            < 40 => StrengthLevel.Weak,
            < 60 => StrengthLevel.Fair,
            < 80 => StrengthLevel.Strong,
            _ => StrengthLevel.VeryStrong,
        };
        return new StrengthResult(level, bits, warnings);
    }

    /// <summary>マスターパスワードとして受け入れられるか（12 文字以上、SR-09）。弱さは警告に留める。</summary>
    public static bool MeetsMasterPasswordMinimum(string password) => password.Length >= MasterPasswordMinLength;

    /// <summary>
    /// 3 文字以上続く同一文字（aaa）や連番・キーボードの並び（abc, 123, qwe）の 3 文字目以降を減点する。
    /// "tt" や "ba" のような 2 文字だけの偶然の並びは普通の単語にも現れるので減点しない。
    /// </summary>
    private static double EffectiveLength(string s, List<string> warnings)
    {
        double length = 0;
        var repeated = false;
        var sequential = false;
        for (var i = 0; i < s.Length; i++)
        {
            if (i >= 2 && s[i] == s[i - 1] && s[i - 1] == s[i - 2])
            {
                length += 0.25;
                repeated = true;
            }
            else if (i >= 2 && IsSequential(Lower(s[i - 2]), Lower(s[i - 1])) && IsSequential(Lower(s[i - 1]), Lower(s[i])))
            {
                length += 0.35;
                sequential = true;
            }
            else
            {
                length += 1;
            }
        }
        if (repeated) warnings.Add("同じ文字の繰り返しが含まれています。");
        if (sequential) warnings.Add("連番やキーボードの並びが含まれています。");
        return length;
    }

    private static char Lower(char c) => char.ToLowerInvariant(c);

    private static bool IsSequential(char a, char b) =>
        Sequences.Any(seq =>
        {
            var i = seq.IndexOf(a);
            return i >= 0 && ((i + 1 < seq.Length && seq[i + 1] == b) || (i > 0 && seq[i - 1] == b));
        });

    private static int LongestCommonWord(string lower) =>
        CommonWords.Where(lower.Contains).Select(w => w.Length).DefaultIfEmpty(0).Max();
}

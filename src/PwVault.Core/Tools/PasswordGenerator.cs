using System.Security.Cryptography;

namespace PwVault.Core.Tools;

public sealed record GeneratorOptions
{
    public const int MinLength = 8;
    public const int MaxLength = 128;

    public int Length { get; init; } = 20;
    public bool Uppercase { get; init; } = true;
    public bool Lowercase { get; init; } = true;
    public bool Digits { get; init; } = true;
    public bool Symbols { get; init; } = true;

    /// <summary>I l 1 | O 0 o などの紛らわしい文字を除外する。</summary>
    public bool ExcludeAmbiguous { get; init; }
}

/// <summary>パスワード生成（FR-07）。乱数は OS の CSPRNG から偏りなく取る（SR-06）。</summary>
public static class PasswordGenerator
{
    public const string UppercaseChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    public const string LowercaseChars = "abcdefghijklmnopqrstuvwxyz";
    public const string DigitChars = "0123456789";
    public const string SymbolChars = "!#$%&()*+,-./:;<=>?@[]^_{|}~";
    public const string AmbiguousChars = "Il1|O0o";

    public static string Generate(GeneratorOptions options)
    {
        if (options.Length is < GeneratorOptions.MinLength or > GeneratorOptions.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(options), $"長さは {GeneratorOptions.MinLength}〜{GeneratorOptions.MaxLength} で指定してください。");

        var sets = new List<string>();
        if (options.Uppercase) sets.Add(UppercaseChars);
        if (options.Lowercase) sets.Add(LowercaseChars);
        if (options.Digits) sets.Add(DigitChars);
        if (options.Symbols) sets.Add(SymbolChars);
        if (options.ExcludeAmbiguous)
            sets = sets.Select(s => new string(s.Where(c => !AmbiguousChars.Contains(c)).ToArray())).ToList();
        if (sets.Count == 0)
            throw new ArgumentException("文字種を 1 つ以上選んでください。", nameof(options));

        var all = string.Concat(sets);
        var chars = new char[options.Length];

        // 選んだ文字種を最低 1 文字ずつ含める
        for (var i = 0; i < sets.Count; i++)
            chars[i] = Pick(sets[i]);
        for (var i = sets.Count; i < chars.Length; i++)
            chars[i] = Pick(all);

        // 先頭に文字種ごとの文字が並ばないようシャッフル（Fisher–Yates）
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        var result = new string(chars);
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()));
        return result;
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

    /// <summary>生成されるパスワードのエントロピー（ビット）の目安。</summary>
    public static double EstimateEntropyBits(GeneratorOptions options)
    {
        var pool = 0;
        if (options.Uppercase) pool += UppercaseChars.Length - (options.ExcludeAmbiguous ? 2 : 0);
        if (options.Lowercase) pool += LowercaseChars.Length - (options.ExcludeAmbiguous ? 2 : 0);
        if (options.Digits) pool += DigitChars.Length - (options.ExcludeAmbiguous ? 2 : 0);
        if (options.Symbols) pool += SymbolChars.Length - (options.ExcludeAmbiguous ? 1 : 0);
        return pool <= 1 ? 0 : options.Length * Math.Log2(pool);
    }
}

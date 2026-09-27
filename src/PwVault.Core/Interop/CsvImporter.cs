using System.Globalization;

namespace PwVault.Core.Interop;

public enum CsvFormat
{
    Unknown,
    /// <summary>Chrome / Edge / Firefox / Safari などのブラウザのエクスポート。</summary>
    Browser,
    Bitwarden,
    KeePassXC,
    /// <summary>このアプリ自身の平文エクスポート。</summary>
    PwVault,
}

public sealed record ImportResult(CsvFormat Format, IReadOnlyList<EntryData> Entries, int SkippedRows);

/// <summary>CSV からの取り込み（FR-12）。先頭行の列名から形式を判定する。</summary>
public static class CsvImporter
{
    public static string FormatName(CsvFormat format) => format switch
    {
        CsvFormat.Browser => "ブラウザ（Chrome / Edge / Firefox）",
        CsvFormat.Bitwarden => "Bitwarden",
        CsvFormat.KeePassXC => "KeePassXC",
        CsvFormat.PwVault => "PwVault",
        _ => "不明",
    };

    public static CsvFormat Detect(IReadOnlyList<string> header)
    {
        var cols = header.Select(h => h.Trim().ToLowerInvariant()).ToHashSet();
        if (cols.Contains("login_password") && cols.Contains("name")) return CsvFormat.Bitwarden;
        if (cols.Contains("title") && cols.Contains("password") && cols.Contains("group")) return CsvFormat.KeePassXC;
        if (cols.Contains("title") && cols.Contains("password") && cols.Contains("tags")) return CsvFormat.PwVault;
        if (cols.Contains("url") && cols.Contains("username") && cols.Contains("password")) return CsvFormat.Browser;
        return CsvFormat.Unknown;
    }

    public static ImportResult Import(string csvText, DateTimeOffset now)
    {
        var rows = Csv.Parse(csvText);
        if (rows.Count == 0)
            return new ImportResult(CsvFormat.Unknown, [], 0);

        var header = rows[0];
        var format = Detect(header);
        if (format == CsvFormat.Unknown)
            return new ImportResult(format, [], rows.Count - 1);

        var index = header.Select((h, i) => (h.Trim().ToLowerInvariant(), i))
            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().i);

        var entries = new List<EntryData>();
        var skipped = 0;
        foreach (var row in rows.Skip(1))
        {
            string Get(string col) => index.TryGetValue(col, out var i) && i < row.Length ? row[i] : "";

            var entry = format switch
            {
                CsvFormat.Bitwarden => FromBitwarden(Get),
                CsvFormat.KeePassXC => FromKeePassXC(Get),
                CsvFormat.PwVault => FromPwVault(Get),
                _ => FromBrowser(Get),
            };

            if (entry is null || (entry.Title.Length == 0 && entry.Username.Length == 0 && entry.Password.Length == 0))
            {
                skipped++;
                continue;
            }
            if (entry.CreatedAt == default) entry.CreatedAt = now;
            if (entry.Title.Length == 0) entry.Title = HostOf(entry.Url) ?? entry.Username;
            entries.Add(entry);
        }

        return new ImportResult(format, entries, skipped);
    }

    private static EntryData? FromBitwarden(Func<string, string> get)
    {
        // Bitwarden は secure note / card なども同じ CSV に入る。ログイン以外は読み飛ばす
        var type = get("type");
        if (type.Length > 0 && !type.Equals("login", StringComparison.OrdinalIgnoreCase))
            return null;

        return new EntryData
        {
            Title = get("name"),
            Username = get("login_username"),
            Password = get("login_password"),
            Url = FirstUrl(get("login_uri")),
            Notes = get("notes"),
            Tags = Tag(get("folder")),
            Favorite = get("favorite") == "1",
        };
    }

    private static EntryData FromKeePassXC(Func<string, string> get)
    {
        var group = get("group");
        // KeePassXC はグループを "Root/仕事/..." のように出力する。先頭の Root は落として全体をタグにする
        if (group.StartsWith("Root/", StringComparison.Ordinal)) group = group[5..];
        else if (group == "Root") group = "";

        return new EntryData
        {
            Title = get("title"),
            Username = get("username"),
            Password = get("password"),
            Url = get("url"),
            Notes = get("notes"),
            Tags = Tag(group),
            CreatedAt = ParseDate(get("created")),
        };
    }

    private static EntryData FromBrowser(Func<string, string> get)
    {
        // Chrome: name,url,username,password,note  /  Firefox: url,username,password,...,timeCreated(ミリ秒)
        var created = long.TryParse(get("timecreated"), out var ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : default;

        return new EntryData
        {
            Title = get("name"),
            Username = get("username"),
            Password = get("password"),
            Url = get("url"),
            Notes = get("note") is { Length: > 0 } note ? note : get("notes"),
            CreatedAt = created,
        };
    }

    private static EntryData FromPwVault(Func<string, string> get) => new()
    {
        Title = get("title"),
        Username = get("username"),
        Password = get("password"),
        Url = get("url"),
        Notes = get("notes"),
        Tags = get("tags").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        CreatedAt = ParseDate(get("created_at")),
        Favorite = get("favorite").Equals("true", StringComparison.OrdinalIgnoreCase),
    };

    private static List<string> Tag(string value) => value.Trim() is { Length: > 0 } t ? [t] : [];

    private static string FirstUrl(string value) =>
        value.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : default;

    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u.Host : null;
}

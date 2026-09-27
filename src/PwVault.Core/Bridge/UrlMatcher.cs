using System.Net;

namespace PwVault.Core.Bridge;

public enum UrlMatch
{
    None = 0,
    /// <summary>保存した URL のサブドメイン（例: 保存 example.com → ページ login.example.com）。</summary>
    Subdomain = 1,
    /// <summary>ホストが一致（先頭の www. は無視）。</summary>
    Exact = 2,
}

/// <summary>
/// ページの URL と保存済みエントリの URL を照合する（ブラウザ自動入力用）。
/// フィッシング対策として、ホストの末尾一致はドット区切りの境界でのみ認め（example.com.evil.test や
/// evilexample.com には一致しない）、https で保存したエントリを http のページには出さない。
/// </summary>
public static class UrlMatcher
{
    public const int MaxResults = 20;

    public static UrlMatch Match(string pageUrl, string entryUrl)
    {
        if (!TryParse(pageUrl, assumeHttps: false, out var page) || !TryParse(entryUrl, assumeHttps: true, out var saved))
            return UrlMatch.None;

        // https で保存したものを http のページで出さない（http で保存したものを https で使うのは可）
        if (saved.Scheme == Uri.UriSchemeHttps && page.Scheme != Uri.UriSchemeHttps)
            return UrlMatch.None;

        // 保存 URL にポートの指定があれば一致を要求。なければページも既定ポートであること
        if (saved.IsDefaultPort ? !page.IsDefaultPort : saved.Port != page.Port)
            return UrlMatch.None;

        var pageHost = Normalize(page.IdnHost);
        var savedHost = Normalize(saved.IdnHost);
        if (pageHost == savedHost)
            return UrlMatch.Exact;

        // IP アドレスや "localhost" のようなドットを含まないホストは完全一致のみ
        if (IPAddress.TryParse(savedHost, out _) || IPAddress.TryParse(pageHost, out _) || !savedHost.Contains('.'))
            return UrlMatch.None;

        return pageHost.EndsWith("." + savedHost, StringComparison.Ordinal) ? UrlMatch.Subdomain : UrlMatch.None;
    }

    /// <summary>ページに合うエントリ（ゴミ箱外）を、完全一致 → お気に入り → タイトル順で返す。</summary>
    public static IReadOnlyList<VaultEntry> FindMatches(IEnumerable<VaultEntry> entries, string pageUrl) =>
        entries
            .Where(e => !e.Data.IsTrashed && (e.Data.Password.Length > 0 || e.Data.Username.Length > 0))
            .Select(e => (Entry: e, Match: Match(pageUrl, e.Data.Url)))
            .Where(x => x.Match != UrlMatch.None)
            .OrderByDescending(x => x.Match)
            .ThenByDescending(x => x.Entry.Data.Favorite)
            .ThenBy(x => x.Entry.Data.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .Select(x => x.Entry)
            .ToList();

    /// <summary>http/https の URL だけを受け付ける。保存 URL はスキーム省略（example.com）を https とみなす。</summary>
    private static bool TryParse(string? url, bool assumeHttps, out Uri uri)
    {
        uri = null!;
        url = url?.Trim();
        if (string.IsNullOrEmpty(url))
            return false;
        if (assumeHttps && !url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(parsed.Host))
            return false;

        uri = parsed;
        return true;
    }

    private static string Normalize(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}

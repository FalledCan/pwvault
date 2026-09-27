using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PwVault.Core.Icons;

namespace PwVault.App.Services;

/// <summary>
/// 各サイトから直接アイコンを取得する（外部のアイコン配信サービスは使わない。問い合わせ先はそのサイト自身だけ）。
/// ・クッキー・リファラーは送らず、読み込む大きさと時間に上限を設ける
/// ・トップページの &lt;link rel="icon"&gt; 等を見て、無ければ /favicon.ico を試す
/// ・SVG はスクリプトを含みうるので取らない。画像として読めるかは呼び出し側で確かめる
/// </summary>
public sealed partial class FaviconFetcher(HttpMessageHandler? handler = null)
{
    private const int MaxHtmlBytes = 512 * 1024;
    private const int PreferredSize = 64;

    private readonly HttpClient _http = CreateClient(handler);

    private static HttpClient CreateClient(HttpMessageHandler? handler)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PwVault/IconFetcher");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,image/*;q=0.9,*/*;q=0.5");
        return client;
    }

    /// <summary>サイトのアイコンを取得する。見つからなければ null。</summary>
    /// <param name="isImage">取得したデータが画像として読めるか（Avalonia で実際に読み込んで確かめる）。</param>
    public async Task<byte[]?> FetchAsync(Uri site, Func<byte[], bool> isImage, CancellationToken ct)
    {
        var origin = new Uri(site.GetLeftPart(UriPartial.Authority) + "/");
        var candidates = new List<Uri>();

        try
        {
            using var page = await _http.GetAsync(origin, HttpCompletionOption.ResponseHeadersRead, ct);
            if (page.IsSuccessStatusCode && page.Content.Headers.ContentType?.MediaType is "text/html" or null)
            {
                var html = Encoding.UTF8.GetString(await ReadLimitedAsync(page.Content, MaxHtmlBytes, ct) ?? []);
                var baseUri = page.RequestMessage?.RequestUri ?? origin;
                candidates.AddRange(ParseIconLinks(html, baseUri));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            // トップページが取れなくても /favicon.ico は試す
        }

        candidates.Add(new Uri(origin, "/favicon.ico"));

        foreach (var uri in candidates.Distinct().Take(4))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) continue;
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                if (type.Contains("svg", StringComparison.OrdinalIgnoreCase) || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    continue;
                var data = await ReadLimitedAsync(response.Content, IconCache.MaxIconBytes, ct);
                if (data is { Length: > 0 } && isImage(data))
                    return data;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
            }
        }
        return null;
    }

    /// <summary>HTML の &lt;link&gt; からアイコン候補を、表示に向いた大きさ（64px 前後）の順に返す。</summary>
    internal static IEnumerable<Uri> ParseIconLinks(string html, Uri baseUri)
    {
        var found = new List<(Uri Uri, int Score)>();
        foreach (Match link in LinkTag().Matches(html))
        {
            var tag = link.Value;
            var rel = Attr(tag, "rel")?.ToLowerInvariant() ?? "";
            if (!rel.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(r => r is "icon" or "apple-touch-icon" or "apple-touch-icon-precomposed"))
                continue;
            var href = Attr(tag, "href");
            var type = Attr(tag, "type") ?? "";
            if (string.IsNullOrWhiteSpace(href) || type.Contains("svg", StringComparison.OrdinalIgnoreCase)
                || href.Split('?')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                || href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Uri.TryCreate(baseUri, WebUtility.HtmlDecode(href.Trim()), out var uri) || uri.Scheme is not ("http" or "https"))
                continue;

            var size = ParseSize(Attr(tag, "sizes")) ?? (rel.Contains("apple-touch-icon") ? 180 : 32);
            found.Add((uri, Math.Abs(size - PreferredSize)));
        }
        return found.OrderBy(f => f.Score).Select(f => f.Uri);
    }

    private static int? ParseSize(string? sizes)
    {
        if (sizes is null) return null;
        var best = sizes.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.ToLowerInvariant().Split('x'))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _))
            .Select(p => int.Parse(p[0]))
            .DefaultIfEmpty(0)
            .MinBy(s => Math.Abs(s - PreferredSize));
        return best > 0 ? best : null;
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, $@"\b{name}\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value) : null;
    }

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTag();

    /// <summary>上限を超えるものは読まずに null（巨大なファイルでメモリを使わない）。</summary>
    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maxBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > maxBytes)
                return maxBytes == MaxHtmlBytes ? buffer.ToArray() : null; // HTML は先頭部分だけで十分
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }
}

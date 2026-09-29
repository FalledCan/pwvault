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

    // 公開のサイト用: 家の中（ルーターなど）や PC 内（localhost）の宛先にはつながない。
    // サイトの HTML や転送で内部の宛先を指定されても、そこへリクエストを送らないため（DNS で内部の IP を返す場合も含む）
    private readonly HttpClient _http = CreateClient(handler, allowPrivate: false);

    // 保存したサイト自体が家の中・PC 内のとき（例: ルーターの管理画面）だけ使う
    private readonly HttpClient _httpLocal = CreateClient(handler, allowPrivate: true);

    private static HttpClient CreateClient(HttpMessageHandler? handler, bool allowPrivate)
    {
        var client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = (context, ct) => ConnectAsync(context, allowPrivate, ct),
        }, disposeHandler: handler is null)
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
        var siteIsInternal = IsInternalHost(origin.Host);
        var http = siteIsInternal ? _httpLocal : _http;

        try
        {
            using var page = await http.GetAsync(origin, HttpCompletionOption.ResponseHeadersRead, ct);
            if (page.IsSuccessStatusCode && page.Content.Headers.ContentType?.MediaType is "text/html" or null)
            {
                var html = Encoding.UTF8.GetString(await ReadLimitedAsync(page.Content, MaxHtmlBytes, ct) ?? []);
                var baseUri = page.RequestMessage?.RequestUri ?? origin;
                // 公開のサイトの HTML に書かれた、家の中・PC 内を指すアイコンの場所は使わない
                candidates.AddRange(ParseIconLinks(html, baseUri).Where(u => siteIsInternal || !IsInternalHost(u.Host)));
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
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
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

    // ------------------------------------------------------------------ 家の中・PC 内の宛先の判定

    /// <summary>接続先の IP を確かめてからつなぐ（名前解決の結果が内部の IP なら、公開サイト用ではつながない）。</summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, bool allowPrivate, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(a => allowPrivate || IsPublicAddress(a)).ToArray();
        if (allowed.Length == 0)
            throw new HttpRequestException("家の中・PC 内の宛先にはつなぎません。");

        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// インターネット上の（公開の）アドレスか。PC 内（127.0.0.1, ::1）、家・会社の中（10.x, 172.16-31.x, 192.168.x, fc00::/7）、
    /// リンクローカル（169.254.x, fe80::）、CGNAT（100.64/10）、未指定・マルチキャストなどは false。
    /// </summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
            return false;

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                     || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                     || b[0] >= 224);
        }

        var v6 = address.GetAddressBytes();
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                 || (v6[0] & 0xFE) == 0xFC); // fc00::/7（ユニークローカル）
    }

    /// <summary>
    /// 家の中・PC 内のサイトを表す名前か（localhost、.local などの名前、ドットの無い名前、内部の IP アドレス）。
    /// 保存したサイト自体がこれなら内部への取得を許し、公開のサイトからこれを指されたら使わない。
    /// </summary>
    internal static bool IsInternalHost(string host)
    {
        host = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(host, out var ip)) return !IsPublicAddress(ip);
        if (!host.Contains('.')) return true; // 例: http://router/
        string[] internalSuffixes = [".localhost", ".local", ".lan", ".home", ".home.arpa", ".internal", ".intranet", ".corp"];
        return host == "localhost" || internalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));
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

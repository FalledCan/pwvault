using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PwVault.Core.Bridge;

/// <summary>
/// ブラウザ拡張 ⇄ 中継（ネイティブメッセージングホスト）⇄ PwVault 本体 の間でやり取りするメッセージ。
/// 形式は Chrome / Firefox のネイティブメッセージングと同じ「4 バイト長（リトルエンディアン）＋ UTF-8 JSON」。
/// </summary>
public sealed class BridgeRequest
{
    public const string TypeStatus = "status";
    public const string TypeList = "list";
    public const string TypeFill = "fill";
    public const string TypeIcon = "icon";

    /// <summary>PwVault の画面（ロック中ならアンロック画面）を前に出す。起動していなければ中継が起動する。</summary>
    public const string TypeOpen = "open";

    public string? Type { get; set; }
    public string? Url { get; set; }
    public string? Id { get; set; }

    /// <summary>icon のとき: タブのアイコン（data:image/...;base64,... 形式）。</summary>
    public string? Icon { get; set; }
}

public sealed class BridgeResponse
{
    public const string ErrorLocked = "locked";
    public const string ErrorNotRunning = "not_running";
    public const string ErrorNoMatch = "no_match";
    public const string ErrorBadRequest = "bad_request";

    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool? Unlocked { get; set; }
    public List<BridgeEntry>? Entries { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    /// <summary>open のとき、PwVault が起動していなかったので中継が起動した。</summary>
    public bool? Started { get; set; }

    public static BridgeResponse Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>候補一覧の 1 件。パスワードは含めない（入力を選んだときに 1 件ずつ渡す）。</summary>
public sealed record BridgeEntry(string Id, string Title, string Username);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BridgeRequest))]
[JsonSerializable(typeof(BridgeResponse))]
internal sealed partial class BridgeJsonContext : JsonSerializerContext;

public static class BridgeMessage
{
    /// <summary>ブラウザ → ホストの上限は 4 GB だが、この用途では 64 KB で十分。大きすぎる入力は拒否する。</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>ホスト → ブラウザは Chrome の制限（1 MB）に合わせる。</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    public static byte[] Serialize(BridgeRequest request) =>
        JsonSerializer.SerializeToUtf8Bytes(request, BridgeJsonContext.Default.BridgeRequest);

    public static byte[] Serialize(BridgeResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, BridgeJsonContext.Default.BridgeResponse);

    public static BridgeRequest? ParseRequest(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeRequest); }
        catch (JsonException) { return null; }
    }

    public static BridgeResponse? ParseResponse(ReadOnlySpan<byte> json)
    {
        try { return JsonSerializer.Deserialize(json, BridgeJsonContext.Default.BridgeResponse); }
        catch (JsonException) { return null; }
    }

    /// <summary>1 メッセージを読む。ストリームの終端なら null。長さが上限を超えたら例外。</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, int maxBytes, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactlyOrEndAsync(stream, header, ct))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > maxBytes)
            throw new InvalidDataException("メッセージが大きすぎます。");

        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(stream, body, ct))
            throw new EndOfStreamException();
        return body;
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                return read == 0 ? false : throw new EndOfStreamException();
            read += n;
        }
        return true;
    }
}

/// <summary>
/// 要求への応答を作る（UI・通信に依存しない部分）。
/// fill では、候補一覧を出したあとにページが別サイトへ移っていても入力しないよう、URL の一致をここで改めて確かめる。
/// </summary>
public static class BridgeHandler
{
    /// <summary>ブラウザから受け取るアイコンとして認める画像形式（SVG はスクリプトを含みうるので受け取らない）。</summary>
    private static readonly string[] IconMimeTypes =
        ["image/png", "image/x-icon", "image/vnd.microsoft.icon", "image/jpeg", "image/gif", "image/webp", "image/bmp"];

    /// <param name="entries">アンロック中なら全エントリ、ロック中なら null。</param>
    /// <param name="storeIcon">icon 要求で受け取ったアイコンを保存する（ホスト名, 画像データ）。</param>
    public static BridgeResponse Handle(BridgeRequest? request, IReadOnlyList<VaultEntry>? entries,
        Action<string, byte[]>? storeIcon = null)
    {
        if (request?.Type is null)
            return BridgeResponse.Fail(BridgeResponse.ErrorBadRequest);

        if (request.Type is BridgeRequest.TypeStatus or BridgeRequest.TypeOpen)
            return new BridgeResponse { Ok = true, Unlocked = entries is not null };

        if (entries is null)
            return BridgeResponse.Fail(BridgeResponse.ErrorLocked);
        if (string.IsNullOrEmpty(request.Url))
            return BridgeResponse.Fail(BridgeResponse.ErrorBadRequest);

        var matches = UrlMatcher.FindMatches(entries, request.Url);
        switch (request.Type)
        {
            case BridgeRequest.TypeList:
                return new BridgeResponse
                {
                    Ok = true,
                    Entries = matches.Select(e => new BridgeEntry(e.Id.ToString("D"), e.Data.Title, e.Data.Username)).ToList(),
                };

            case BridgeRequest.TypeFill:
                if (!Guid.TryParse(request.Id, out var id))
                    return BridgeResponse.Fail(BridgeResponse.ErrorBadRequest);
                var entry = matches.FirstOrDefault(e => e.Id == id);
                return entry is null
                    ? BridgeResponse.Fail(BridgeResponse.ErrorNoMatch)
                    : new BridgeResponse { Ok = true, Username = entry.Data.Username, Password = entry.Data.Password };

            case BridgeRequest.TypeIcon:
                // 保存済みエントリがあるサイトのアイコンだけ受け取る（閲覧履歴を集めない）
                if (matches.Count == 0)
                    return BridgeResponse.Fail(BridgeResponse.ErrorNoMatch);
                if (storeIcon is null || !TryDecodeIcon(request.Icon, out var image))
                    return BridgeResponse.Fail(BridgeResponse.ErrorBadRequest);
                // 保存 URL のホスト（login.example.com で開いても example.com で保存していればそちら）に紐づける
                foreach (var host in matches.Select(e => UrlMatcher.HostKey(e.Data.Url)).OfType<string>().Distinct())
                    storeIcon(host, image);
                return new BridgeResponse { Ok = true };

            default:
                return BridgeResponse.Fail(BridgeResponse.ErrorBadRequest);
        }
    }

    internal static bool TryDecodeIcon(string? dataUrl, out byte[] image)
    {
        image = [];
        if (dataUrl is null || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) return false;
        var meta = dataUrl[5..comma].Split(';');
        if (!IconMimeTypes.Contains(meta[0].ToLowerInvariant()) || !meta.Contains("base64", StringComparer.OrdinalIgnoreCase))
            return false;
        try
        {
            image = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }
        return image.Length is > 0 and <= Icons.IconCache.MaxIconBytes;
    }
}

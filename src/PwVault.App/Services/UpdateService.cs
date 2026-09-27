using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PwVault.Core.Updates;

namespace PwVault.App.Services;

/// <summary>更新の情報（GitHub の最新リリース）。</summary>
public sealed record UpdateInfo(string Version, string PageUrl, string Notes, string? AssetName, Uri? AssetUrl, Uri? SignatureUrl, long AssetSize)
{
    /// <summary>この OS 向けに自動で入れ替えられるファイルがあるか。</summary>
    public bool CanInstall => AssetUrl is not null && SignatureUrl is not null;
}

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 更新の確認・ダウンロード・署名の検証・入れ替え。
/// ・確認先は GitHub の最新リリース（API）だけ。設定でオフにすると通信しない
/// ・ダウンロードしたファイルは、アプリに埋め込んだ公開鍵で署名（版・ファイル名・SHA-256）を確かめてから使う
/// ・Windows は実行中の exe を .old に改名して新しい exe を置き、再起動する（Windows は実行中の exe の改名を許す）
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "FalledCan/pwvault";
    private const long MaxAssetBytes = 300L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string? _publicKeyBase64;

    public UpdateService(HttpMessageHandler? handler = null, string? publicKeyBase64 = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PwVault", CurrentVersion));
        _publicKeyBase64 = publicKeyBase64;
    }

    /// <summary>実行中のアプリの版（例: 0.4.0）。</summary>
    public static string CurrentVersion
    {
        get
        {
            var info = (Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly)
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return ReleaseVersion.Parse(info)?.ToString(3) ?? "0.0.0";
        }
    }

    /// <summary>この OS 向けのリリースファイル名。対応していなければ null（リリースページを開くだけにする）。</summary>
    public static string? PlatformAssetName(string version)
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64)
            return $"PwVault-{version}-win-x64.exe";
        return null;
    }

    /// <summary>新しい版があれば情報を返す。無ければ null。</summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        JsonNode? json;
        try { json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)); }
        catch (JsonException ex) { throw new UpdateException("更新情報を読み取れませんでした。", ex); }

        var tag = json?["tag_name"]?.GetValue<string>();
        if (json?["draft"]?.GetValue<bool>() == true || json?["prerelease"]?.GetValue<bool>() == true
            || !ReleaseVersion.IsNewer(tag, currentVersion))
            return null;

        var version = ReleaseVersion.Parse(tag)!.ToString(3);
        var assets = json!["assets"]?.AsArray() ?? [];
        var assetName = PlatformAssetName(version);
        var asset = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == assetName);
        var signature = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == assetName + ".sig");

        return new UpdateInfo(
            version,
            json["html_url"]?.GetValue<string>() ?? $"https://github.com/{Repository}/releases/latest",
            json["body"]?.GetValue<string>() ?? "",
            asset is null ? null : assetName,
            UriOf(asset), UriOf(signature),
            asset?["size"]?.GetValue<long>() ?? 0);
    }

    private static Uri? UriOf(JsonNode? asset) =>
        asset?["browser_download_url"]?.GetValue<string>() is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    /// <summary>ダウンロードして署名を確かめる。確かめられたファイルのパスを返す（失敗したら消して例外）。</summary>
    public async Task<string> DownloadAndVerifyAsync(UpdateInfo info, string targetDir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!info.CanInstall)
            throw new UpdateException("この OS 向けの更新ファイルがありません。");
        if (info.AssetSize > MaxAssetBytes)
            throw new UpdateException("更新ファイルが大きすぎます。");

        var signature = await _http.GetStringAsync(info.SignatureUrl, ct);
        if (signature.Length > 1024)
            throw new UpdateException("署名ファイルが不正です。");

        Directory.CreateDirectory(targetDir);
        var path = Path.Combine(targetDir, $".{info.AssetName}.download");
        try
        {
            using (var response = await _http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? info.AssetSize;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long written = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    written += n;
                    if (written > MaxAssetBytes) throw new UpdateException("更新ファイルが大きすぎます。");
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    if (total > 0) progress?.Report((double)written / total);
                }
                file.Flush(flushToDisk: true);
            }

            await using (var stream = File.OpenRead(path))
                if (!ReleaseSignature.Verify(info.Version, info.AssetName!, stream, signature, _publicKeyBase64))
                    throw new UpdateException("更新ファイルの署名を確認できませんでした。改ざんされている可能性があるため更新を中止しました。");
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>
    /// 実行中の exe を新しいものに入れ替える（Windows）。実行中の exe は .old に改名し、次回起動時に消す。
    /// 途中で失敗したら元に戻す。
    /// </summary>
    public static void ReplaceExecutable(string newFile, string exePath)
    {
        var old = exePath + ".old";
        TryDelete(old);
        File.Move(exePath, old);
        try
        {
            File.Move(newFile, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>前回の更新で残った古い exe を消す（起動時に呼ぶ）。</summary>
    public static void CleanupAfterUpdate(string exePath) => TryDelete(exePath + ".old");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

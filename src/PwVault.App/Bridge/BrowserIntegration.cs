using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace PwVault.App.Bridge;

public enum IntegrationStatus
{
    NotRegistered,
    Registered,
    /// <summary>登録済みだが、どれかのブラウザの登録が欠けている、または別の場所の exe を指している（exe を移動した等）。</summary>
    PathMismatch,
}

/// <summary>
/// ブラウザ連携の登録（現在のユーザーのみ）。
/// ・Windows: ホストのマニフェストを %LOCALAPPDATA%\PwVault\NativeMessaging に書き、Chrome / Edge / Firefox のレジストリ（HKCU）に場所を登録する
/// ・macOS: 各ブラウザが決めているフォルダ（~/Library/Application Support/…/NativeMessagingHosts）にマニフェストを直接書く
/// ・どちらも、アプリに埋め込んだ拡張機能を &lt;DataDir&gt;/BrowserExtension/{chromium,firefox} に展開する
/// </summary>
public sealed class BrowserIntegration
{
    private static readonly string[] ChromiumKeys =
    [
        @"Google\Chrome\NativeMessagingHosts\" + NativeHost.HostName,
        @"Microsoft\Edge\NativeMessagingHosts\" + NativeHost.HostName,
    ];
    private const string FirefoxKey = @"Mozilla\NativeMessagingHosts\" + NativeHost.HostName;

    private static readonly string[] MacChromiumDirs = ["Google/Chrome/NativeMessagingHosts", "Microsoft Edge/NativeMessagingHosts"];
    private const string MacFirefoxDir = "Mozilla/NativeMessagingHosts";

    private readonly string _registryBase;
    private readonly string _macSupportRoot;

    /// <param name="registryBase">Windows: 登録先（テストでは別のキーにする）。</param>
    /// <param name="macSupportRoot">macOS: ~/Library/Application Support に当たる場所（テストでは一時フォルダにする）。</param>
    public BrowserIntegration(string? dataDir = null, string registryBase = "Software", string? macSupportRoot = null)
    {
        DataDir = dataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PwVault");
        _registryBase = registryBase;
        _macSupportRoot = macSupportRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
    }

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public string DataDir { get; }
    public string ManifestDir => Path.Combine(DataDir, "NativeMessaging");
    public string ChromiumExtensionDir => Path.Combine(DataDir, "BrowserExtension", "chromium");
    public string FirefoxExtensionDir => Path.Combine(DataDir, "BrowserExtension", "firefox");
    private string ChromiumManifest => Path.Combine(ManifestDir, NativeHost.HostName + ".chromium.json");
    private string FirefoxManifest => Path.Combine(ManifestDir, NativeHost.HostName + ".firefox.json");

    /// <summary>macOS で各ブラウザのマニフェストを書く場所（Chrome, Edge, Firefox の順）。</summary>
    public IReadOnlyList<string> MacManifestPaths =>
        MacChromiumDirs.Append(MacFirefoxDir).Select(d => Path.Combine(_macSupportRoot, d, NativeHost.HostName + ".json")).ToList();

    public void Register(string exePath)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(ManifestDir);
            WriteChromiumManifest(ChromiumManifest, exePath);
            WriteFirefoxManifest(FirefoxManifest, exePath);
            foreach (var key in ChromiumKeys)
                SetDefault(key, ChromiumManifest);
            SetDefault(FirefoxKey, FirefoxManifest);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var paths = MacManifestPaths;
            for (var i = 0; i < paths.Count; i++)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths[i])!);
                if (i < MacChromiumDirs.Length) WriteChromiumManifest(paths[i], exePath);
                else WriteFirefoxManifest(paths[i], exePath);
            }
        }
        else
        {
            throw new PlatformNotSupportedException("ブラウザ連携はこの OS には対応していません。");
        }

        ExtractExtension();
    }

    public void Unregister()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var key in ChromiumKeys.Append(FirefoxKey))
                Registry.CurrentUser.DeleteSubKeyTree($@"{_registryBase}\{key}", throwOnMissingSubKey: false);
            if (Directory.Exists(ManifestDir))
                Directory.Delete(ManifestDir, recursive: true);
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var path in MacManifestPaths.Where(File.Exists))
                File.Delete(path);
        }
        // 拡張機能フォルダはブラウザが読み込んでいる可能性があるので残す
    }

    /// <summary>
    /// 登録状態。Chrome・Edge・Firefox のすべてについて「登録 → マニフェスト → この exe」がつながっていれば Registered。
    /// どれか 1 つでも欠けている・古い exe を指していれば PathMismatch（起動時に登録し直す）。
    /// </summary>
    public IntegrationStatus GetStatus(string exePath)
    {
        IEnumerable<string?> manifests = OperatingSystem.IsWindows()
            ? ChromiumKeys.Append(FirefoxKey).Select(RegisteredManifest)
            : MacManifestPaths;
        var states = manifests.Select(m => CheckManifest(m, exePath)).ToList();
        if (states.All(s => s == IntegrationStatus.NotRegistered))
            return IntegrationStatus.NotRegistered;
        return states.All(s => s == IntegrationStatus.Registered) ? IntegrationStatus.Registered : IntegrationStatus.PathMismatch;
    }

    private static IntegrationStatus CheckManifest(string? manifestPath, string exePath)
    {
        if (manifestPath is null || !File.Exists(manifestPath))
            return IntegrationStatus.NotRegistered;
        try
        {
            var registered = JsonNode.Parse(File.ReadAllText(manifestPath))?["path"]?.GetValue<string>();
            return string.Equals(registered, exePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? IntegrationStatus.Registered
                : IntegrationStatus.PathMismatch;
        }
        catch (JsonException)
        {
            return IntegrationStatus.PathMismatch;
        }
    }

    [SupportedOSPlatform("windows")]
    private string? RegisteredManifest(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{_registryBase}\{subKey}");
        return key?.GetValue(null) as string;
    }

    [SupportedOSPlatform("windows")]
    private void SetDefault(string subKey, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{_registryBase}\{subKey}");
        key.SetValue(null, value);
    }

    /// <summary>アプリに埋め込んだ拡張機能をブラウザごとのフォルダに書き出す（manifest はブラウザ別のものを manifest.json にする）。</summary>
    public void ExtractExtension()
    {
        var assembly = typeof(BrowserIntegration).Assembly;
        const string prefix = "ext/";
        var files = assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        foreach (var (dir, manifest) in new[] { (ChromiumExtensionDir, "manifest.chromium.json"), (FirefoxExtensionDir, "manifest.firefox.json") })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            foreach (var name in files)
            {
                var relative = name[prefix.Length..];
                if (relative.StartsWith("manifest.", StringComparison.Ordinal) && relative != manifest)
                    continue;
                var target = Path.Combine(dir, relative == manifest ? "manifest.json" : relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var src = assembly.GetManifestResourceStream(name)!;
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
        }
    }

    private static void WriteChromiumManifest(string path, string exePath) =>
        WriteManifest(path, exePath, "allowed_origins", $"chrome-extension://{NativeHost.ChromiumExtensionId}/");

    private static void WriteFirefoxManifest(string path, string exePath) =>
        WriteManifest(path, exePath, "allowed_extensions", NativeHost.FirefoxExtensionId);

    private static void WriteManifest(string path, string exePath, string allowKey, string allowValue)
    {
        var json = new JsonObject
        {
            ["name"] = NativeHost.HostName,
            ["description"] = "PwVault ブラウザ連携",
            ["path"] = exePath,
            ["type"] = "stdio",
            [allowKey] = new JsonArray(allowValue),
        };
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

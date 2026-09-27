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
/// ブラウザ連携の登録（Windows・現在のユーザーのみ）。
/// ・ネイティブメッセージングホストのマニフェストを %LOCALAPPDATA%\PwVault\NativeMessaging に書き、
///   Chrome / Edge / Firefox のレジストリ（HKCU）にその場所を登録する
/// ・exe に埋め込んだ拡張機能を %LOCALAPPDATA%\PwVault\BrowserExtension\{chromium,firefox} に展開する
/// </summary>
public sealed class BrowserIntegration
{
    private static readonly string[] ChromiumKeys =
    [
        @"Google\Chrome\NativeMessagingHosts\" + NativeHost.HostName,
        @"Microsoft\Edge\NativeMessagingHosts\" + NativeHost.HostName,
    ];
    private const string FirefoxKey = @"Mozilla\NativeMessagingHosts\" + NativeHost.HostName;

    private readonly string _registryBase;

    public BrowserIntegration(string? dataDir = null, string registryBase = "Software")
    {
        DataDir = dataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PwVault");
        _registryBase = registryBase;
    }

    public string DataDir { get; }
    public string ManifestDir => Path.Combine(DataDir, "NativeMessaging");
    public string ChromiumExtensionDir => Path.Combine(DataDir, "BrowserExtension", "chromium");
    public string FirefoxExtensionDir => Path.Combine(DataDir, "BrowserExtension", "firefox");
    private string ChromiumManifest => Path.Combine(ManifestDir, NativeHost.HostName + ".chromium.json");
    private string FirefoxManifest => Path.Combine(ManifestDir, NativeHost.HostName + ".firefox.json");

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Register(string exePath)
    {
        Directory.CreateDirectory(ManifestDir);
        WriteManifest(ChromiumManifest, exePath, "allowed_origins", $"chrome-extension://{NativeHost.ChromiumExtensionId}/");
        WriteManifest(FirefoxManifest, exePath, "allowed_extensions", NativeHost.FirefoxExtensionId);

        foreach (var key in ChromiumKeys)
            SetDefault(key, ChromiumManifest);
        SetDefault(FirefoxKey, FirefoxManifest);

        ExtractExtension();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Unregister()
    {
        foreach (var key in ChromiumKeys.Append(FirefoxKey))
            Registry.CurrentUser.DeleteSubKeyTree($@"{_registryBase}\{key}", throwOnMissingSubKey: false);
        if (Directory.Exists(ManifestDir))
            Directory.Delete(ManifestDir, recursive: true);
        // 拡張機能フォルダはブラウザが読み込んでいる可能性があるので残す
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    /// <summary>
    /// 登録状態。Chrome・Edge・Firefox のすべてについて「レジストリ → マニフェスト → この exe」がつながっていれば Registered。
    /// どれか 1 つでも欠けている・古い exe を指していれば PathMismatch（起動時に登録し直す）。
    /// </summary>
    public IntegrationStatus GetStatus(string exePath)
    {
        var states = ChromiumKeys.Append(FirefoxKey).Select(k => CheckKey(k, exePath)).ToList();
        if (states.All(s => s == IntegrationStatus.NotRegistered))
            return IntegrationStatus.NotRegistered;
        return states.All(s => s == IntegrationStatus.Registered) ? IntegrationStatus.Registered : IntegrationStatus.PathMismatch;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private IntegrationStatus CheckKey(string subKey, string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{_registryBase}\{subKey}");
        if (key?.GetValue(null) is not string manifestPath || !File.Exists(manifestPath))
            return IntegrationStatus.NotRegistered;
        try
        {
            var registered = JsonNode.Parse(File.ReadAllText(manifestPath))?["path"]?.GetValue<string>();
            return string.Equals(registered, exePath, StringComparison.OrdinalIgnoreCase)
                ? IntegrationStatus.Registered
                : IntegrationStatus.PathMismatch;
        }
        catch (JsonException)
        {
            return IntegrationStatus.PathMismatch;
        }
    }

    /// <summary>exe に埋め込んだ拡張機能をブラウザごとのフォルダに書き出す（manifest はブラウザ別のものを manifest.json にする）。</summary>
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

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void SetDefault(string subKey, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{_registryBase}\{subKey}");
        key.SetValue(null, value);
    }
}

using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using PwVault.App.Bridge;
using PwVault.App.Services;

namespace PwVault.App.Tests;

/// <summary>macOS 固有の部分。GitHub Actions の macOS 環境で動かす（Windows では飛ばす）。</summary>
public class MacTests
{
    [AvaloniaFact]
    public void Register_WritesManifestFiles_ForEachBrowser()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS 専用");
        using var h = new Harness();
        var integration = h.Integration;
        var exe = "/Applications/PwVault.app/Contents/MacOS/PwVault";

        Assert.Equal(IntegrationStatus.NotRegistered, integration.GetStatus(exe));
        integration.Register(exe);
        Assert.Equal(IntegrationStatus.Registered, integration.GetStatus(exe));

        var paths = integration.MacManifestPaths;
        Assert.Equal(3, paths.Count);
        Assert.Contains(paths, p => p.Contains("Google/Chrome/NativeMessagingHosts"));
        Assert.Contains(paths, p => p.Contains("Microsoft Edge/NativeMessagingHosts"));
        Assert.Contains(paths, p => p.Contains("Mozilla/NativeMessagingHosts"));
        foreach (var path in paths)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(path))!;
            Assert.Equal(exe, manifest["path"]!.GetValue<string>());
        }
        Assert.Equal($"chrome-extension://{NativeHost.ChromiumExtensionId}/", JsonNode.Parse(File.ReadAllText(paths[0]))!["allowed_origins"]![0]!.GetValue<string>());
        Assert.Equal(NativeHost.FirefoxExtensionId, JsonNode.Parse(File.ReadAllText(paths[2]))!["allowed_extensions"]![0]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(integration.ChromiumExtensionDir, "manifest.json")));

        // 1 つ消えたら要修復、別の場所の exe を指していても要修復
        File.Delete(paths[1]);
        Assert.Equal(IntegrationStatus.PathMismatch, integration.GetStatus(exe));
        integration.Register(exe);
        Assert.Equal(IntegrationStatus.PathMismatch, integration.GetStatus("/Users/x/Downloads/PwVault.app/Contents/MacOS/PwVault"));

        integration.Unregister();
        Assert.Equal(IntegrationStatus.NotRegistered, integration.GetStatus(exe));
        Assert.All(paths, p => Assert.False(File.Exists(p)));
    }

    /// <summary>実際のクリップボードを使う（macOS の CI では実行する）。</summary>
    [AvaloniaFact(Explicit = true)]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Clipboard_MarksConcealed_AndClearsOnlyOwnValue()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS 専用");
        using var service = new ClipboardService(() => IntPtr.Zero);

        service.Copy("pwvault-secret-1", clearAfterSeconds: 60);
        Assert.Equal("pwvault-secret-1", MacPasteboard.GetText());
        Assert.True(MacPasteboard.HasConcealedMarker());

        service.ClearIfOwned();
        Assert.Null(MacPasteboard.GetText());

        // 他のアプリが上書きした値は消さない
        service.Copy("pwvault-secret-2", clearAfterSeconds: 60);
        MacPasteboard.SetConcealedText("user copied something else");
        service.ClearIfOwned();
        Assert.Equal("user copied something else", MacPasteboard.GetText());
        MacPasteboard.Clear();
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void ScreenLockCheck_DoesNotThrow()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS 専用");
        _ = MacSession.IsScreenLocked();
    }
}

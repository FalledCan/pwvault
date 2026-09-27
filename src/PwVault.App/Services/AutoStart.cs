using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace PwVault.App.Services;

/// <summary>
/// PC にサインインしたときの自動起動（現在のユーザーのみ）。起動時は通知領域に隠れた状態で始まる（<see cref="BackgroundArgument"/>）。
/// ・Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run に登録
/// ・macOS: ~/Library/LaunchAgents に launchd の設定ファイルを置く（RunAtLoad）
/// </summary>
public sealed class AutoStart
{
    public const string BackgroundArgument = "--background";
    private const string ValueName = "PwVault";
    private const string MacLabel = "io.github.falledcan.pwvault";

    private readonly string _runKey;
    private readonly string _launchAgentsDir;

    /// <param name="runKey">Windows: 登録先のキー（テストでは別のキーにする）。</param>
    /// <param name="launchAgentsDir">macOS: ~/Library/LaunchAgents に当たる場所（テストでは一時フォルダにする）。</param>
    public AutoStart(string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run", string? launchAgentsDir = null)
    {
        _runKey = runKey;
        _launchAgentsDir = launchAgentsDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
    }

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public string MacPlistPath => Path.Combine(_launchAgentsDir, MacLabel + ".plist");

    /// <summary>登録されているか（別の場所の exe を指していても true）。</summary>
    public bool IsEnabled() => RegisteredCommand() is not null;

    /// <summary>登録されていて、今の実行ファイルを指しているか。</summary>
    public bool IsEnabledFor(string exePath) =>
        RegisteredCommand() is { } cmd && cmd.Contains(
            OperatingSystem.IsWindows() ? $"\"{exePath}\"" : $"<string>{SecurityElement.Escape(exePath)}</string>",
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Enable(string exePath)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(_runKey);
            key.SetValue(ValueName, $"\"{exePath}\" {BackgroundArgument}");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(_launchAgentsDir);
            File.WriteAllText(MacPlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>{MacLabel}</string>
                  <key>ProgramArguments</key>
                  <array>
                    <string>{SecurityElement.Escape(exePath)}</string>
                    <string>{BackgroundArgument}</string>
                  </array>
                  <key>RunAtLoad</key><true/>
                  <key>ProcessType</key><string>Interactive</string>
                </dict>
                </plist>
                """);
        }
        else
        {
            throw new PlatformNotSupportedException("自動起動はこの OS には対応していません。");
        }
    }

    public void Disable()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        else if (OperatingSystem.IsMacOS() && File.Exists(MacPlistPath))
        {
            File.Delete(MacPlistPath);
        }
    }

    private string? RegisteredCommand()
    {
        if (OperatingSystem.IsWindows())
            return ReadRunValue();
        if (OperatingSystem.IsMacOS() && File.Exists(MacPlistPath))
            return File.ReadAllText(MacPlistPath);
        return null;
    }

    [SupportedOSPlatform("windows")]
    private string? ReadRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKey);
        return key?.GetValue(ValueName) as string;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using PwVault.Core.Tools;

namespace PwVault.App.Services;

/// <summary>
/// アプリの設定（機密ではない値のみ）。%APPDATA%\PwVault\settings.json に平文で保存する。
/// KDF パラメータは保管庫ヘッダ側にあり、ここには持たない。
/// </summary>
public sealed class AppSettings
{
    public string? VaultPath { get; set; }
    public int AutoLockMinutes { get; set; } = 5;
    public int ClipboardClearSeconds { get; set; } = 30;
    public int BackupGenerations { get; set; } = 5;
    public EntrySort Sort { get; set; } = EntrySort.Title;
    public GeneratorOptions Generator { get; set; } = new();

    /// <summary>ブラウザ連携（右クリックからの自動入力）を有効にしているか。</summary>
    public bool BrowserIntegration { get; set; }

    /// <summary>
    /// ブラウザ拡張から届かなかったサイトのアイコンを、PwVault が各サイトから直接取得するか。
    /// オフにすると PwVault 本体はネットワーク通信を一切しない（要件 NFR-05 の状態）。
    /// </summary>
    public bool FetchSiteIcons { get; set; } = true;

    /// <summary>一覧のダブルクリックでサイトを開くか。</summary>
    public bool DoubleClickOpensUrl { get; set; } = true;

    /// <summary>×ボタンで終了せず、通知領域（タスクトレイ）に隠すか。</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>起動時と 1 日 1 回、GitHub の最新リリースを確認するか。</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Windows Hello でのアンロックを、マスターパスワードなしで続けられる日数。過ぎたらマスターパスワードが必要。</summary>
    public int QuickUnlockDays { get; set; } = 14;

    public static string DefaultVaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PwVault", "vault.pwv");
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

public sealed class AppSettingsStore
{
    private readonly string _path;

    public AppSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PwVault", "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return Normalize(JsonSerializer.Deserialize(File.ReadAllText(_path), AppSettingsJsonContext.Default.AppSettings) ?? new());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 設定が壊れていても既定値で起動する（機密は含まないので失っても問題ない）
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
        File.Move(tmp, _path, overwrite: true);
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.AutoLockMinutes = Math.Clamp(s.AutoLockMinutes, 1, 240);
        s.ClipboardClearSeconds = Math.Clamp(s.ClipboardClearSeconds, 5, 600);
        s.BackupGenerations = Math.Clamp(s.BackupGenerations, 0, Core.Storage.AtomicFileStore.MaxBackupGenerations);
        s.Generator ??= new GeneratorOptions();
        s.QuickUnlockDays = Math.Clamp(s.QuickUnlockDays, 1, 90);
        return s;
    }
}

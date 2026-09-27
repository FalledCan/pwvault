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
        return s;
    }
}

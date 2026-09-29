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

    /// <summary>ゲーム・アプリのログイン画面への自動入力（ショートカットキー）を使うか。</summary>
    public bool AutoTypeEnabled { get; set; }

    /// <summary>自動入力のショートカットキー（<see cref="AutoTypeHotKey.Presets"/> の Id）。</summary>
    public string AutoTypeHotKeyId { get; set; } = "CtrlAltA";

    /// <summary>紐付けたアプリの画面が前に出たとき、エントリの設定（候補を出す・自動で入力する）に合わせて動くか。</summary>
    public bool AutoTypeOnOpenEnabled { get; set; } = true;

    /// <summary>「自動で入力する」エントリで、アプリが前に出てから入力するまでの秒数（1〜15）。</summary>
    public int AutoTypeOnOpenSeconds { get; set; } = 3;

    /// <summary>チュートリアルを一度見た（または閉じた）か。保管庫を作った直後に自動で出すのは、まだのときだけ。</summary>
    public bool TutorialSeen { get; set; }

    /// <summary>画面の明るさ（OS に合わせる / ライト / ダーク）。</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>ボタンなどの色。</summary>
    public AccentColor Accent { get; set; } = AccentColor.System;

    public static string DefaultVaultPath =>
        Path.Combine(OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents")
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PwVault", "vault.pwv");
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
        s.AutoTypeOnOpenSeconds = Math.Clamp(s.AutoTypeOnOpenSeconds, 1, 15);
        if (!Enum.IsDefined(s.Theme)) s.Theme = ThemeMode.System;
        if (!Enum.IsDefined(s.Accent)) s.Accent = AccentColor.System;
        return s;
    }
}

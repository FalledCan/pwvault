using System.Text.RegularExpressions;

namespace PwVault.App.Services;

/// <summary>保管庫の保存先の種類。</summary>
public enum StorageKind { Local, GoogleDrive, Nextcloud, Other }

/// <summary>
/// クラウドの同期フォルダ（Google ドライブ パソコン版・Nextcloud デスクトップ）を探す。
/// PwVault はクラウドと直接通信せず、これらのアプリが PC 上に作るフォルダに保管庫を置くだけ。
/// テストでは探す場所（ホーム・AppData・ドライブ一覧）を一時フォルダに差し替える。
/// </summary>
public sealed partial class SyncFolderLocator
{
    /// <summary>同期フォルダの中で保管庫を置くサブフォルダ。</summary>
    public const string SubFolder = "PwVault";

    private readonly string _home;
    private readonly string _appData;
    private readonly string _documents;
    private readonly Func<IEnumerable<string>> _driveRoots;

    public SyncFolderLocator(string? home = null, string? appData = null, string? documents = null,
        Func<IEnumerable<string>>? driveRoots = null)
    {
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _appData = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _documents = documents ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(_home, "Documents")
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        _driveRoots = driveRoots ?? DefaultDriveRoots;
    }

    /// <summary>「この PC」を選んだときの保存先フォルダ（ドキュメント\PwVault）。</summary>
    public string LocalFolder => Path.Combine(_documents, SubFolder);

    /// <summary>その種類の同期フォルダ（見つからなければ null）。Local はドキュメント。</summary>
    public string? RootOf(StorageKind kind) => kind switch
    {
        StorageKind.Local => _documents,
        StorageKind.GoogleDrive => FindGoogleDrive(),
        StorageKind.Nextcloud => FindNextcloud(),
        _ => null,
    };

    /// <summary>その種類を選んだときの保管庫ファイルの場所（ファイル名は今のものを引き継ぐ）。</summary>
    public string? TargetPath(StorageKind kind, string fileName) =>
        RootOf(kind) is { } root ? Path.Combine(root, SubFolder, fileName) : null;

    /// <summary>保管庫が今どこにあるか（同期フォルダの中か）。</summary>
    public StorageKind KindOf(string vaultPath)
    {
        var full = Path.GetFullPath(vaultPath);
        foreach (var kind in new[] { StorageKind.GoogleDrive, StorageKind.Nextcloud })
            if (RootOf(kind) is { } root && IsUnder(full, root)) return kind;
        return IsUnder(full, LocalFolder) ? StorageKind.Local : StorageKind.Other;
    }

    public static string DisplayName(StorageKind kind) => kind switch
    {
        StorageKind.Local => "この PC",
        StorageKind.GoogleDrive => "Google ドライブ",
        StorageKind.Nextcloud => "Nextcloud",
        _ => "その他の場所",
    };

    /// <summary>同期フォルダに既にある保管庫（2 台目の PC の初回画面で「開く」を出す）。</summary>
    public IReadOnlyList<(StorageKind Kind, string Path)> FindExistingVaults()
    {
        var found = new List<(StorageKind, string)>();
        foreach (var kind in new[] { StorageKind.GoogleDrive, StorageKind.Nextcloud })
        {
            if (RootOf(kind) is not { } root) continue;
            var dir = Path.Combine(root, SubFolder);
            try
            {
                if (!Directory.Exists(dir)) continue;
                var preferred = Path.Combine(dir, "vault.pwv");
                var path = File.Exists(preferred) ? preferred : Directory.EnumerateFiles(dir, "*.pwv")
                    .Where(p => Path.GetExtension(p).Equals(".pwv", StringComparison.OrdinalIgnoreCase))
                    .Order(StringComparer.Ordinal).FirstOrDefault();
                if (path is not null) found.Add((kind, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    // ------------------------------------------------------------------ Google ドライブ（パソコン版）

    /// <summary>
    /// Windows: 仮想ドライブ（例 G:\）の「マイドライブ / My Drive」、またはミラーモードの %USERPROFILE%\My Drive。
    /// macOS: ~/Library/CloudStorage/GoogleDrive-アカウント/マイドライブ（My Drive）。
    /// </summary>
    public string? FindGoogleDrive()
    {
        string[] names = ["マイドライブ", "My Drive"];
        var candidates = new List<string>();
        if (OperatingSystem.IsMacOS())
        {
            var cloud = Path.Combine(_home, "Library", "CloudStorage");
            foreach (var account in SafeDirectories(cloud, "GoogleDrive-*"))
                candidates.AddRange(names.Select(n => Path.Combine(account, n)));
        }
        else
        {
            foreach (var root in _driveRoots())
                candidates.AddRange(names.Select(n => Path.Combine(root, n)));
        }
        candidates.AddRange(names.Select(n => Path.Combine(_home, n)));
        return candidates.FirstOrDefault(SafeExists);
    }

    // ------------------------------------------------------------------ Nextcloud

    /// <summary>Nextcloud デスクトップの設定ファイル（nextcloud.cfg）に書かれた同期フォルダ。無ければ ~/Nextcloud。</summary>
    public string? FindNextcloud()
    {
        var cfg = OperatingSystem.IsMacOS()
            ? Path.Combine(_home, "Library", "Preferences", "Nextcloud", "nextcloud.cfg")
            : Path.Combine(_appData, "Nextcloud", "nextcloud.cfg");
        var candidates = new List<string>();
        try
        {
            if (File.Exists(cfg))
                foreach (var line in File.ReadLines(cfg))
                    if (LocalPathLine().Match(line.Trim()) is { Success: true } m)
                        candidates.Add(m.Groups[1].Value.Trim().Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (OperatingSystem.IsMacOS())
            candidates.AddRange(SafeDirectories(Path.Combine(_home, "Library", "CloudStorage"), "Nextcloud*"));
        candidates.Add(Path.Combine(_home, "Nextcloud"));
        return candidates.FirstOrDefault(c => c.Length > 0 && SafeExists(c));
    }

    // 例: 0\Folders\1\localPath=C:/Users/me/Nextcloud/
    [GeneratedRegex(@"^\d+\\(?:Folders|FoldersWithPlaceholders|Multifolders)\\\d+\\localPath=(.+)$")]
    private static partial Regex LocalPathLine();

    // ------------------------------------------------------------------ 共通

    private static IEnumerable<string> DefaultDriveRoots()
    {
        if (!OperatingSystem.IsWindows()) return [];
        // Google ドライブの仮想ドライブは「ローカル ディスク」扱い。つながっていないネットワークドライブや
        // CD ドライブは、存在の確認だけで数秒以上待たされることがある（画面が固まる）ので見ない
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed)
            .Select(d => d.RootDirectory.FullName);
    }

    private static bool SafeExists(string dir)
    {
        try { return Directory.Exists(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> SafeDirectories(string dir, string pattern)
    {
        try { return SafeExists(dir) ? Directory.GetDirectories(dir, pattern) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsUnder(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(r, comparison);
    }
}

namespace PwVault.Core.Storage;

/// <summary>
/// 書き込み途中の破損を防ぐ保存（要件 6.3）。
/// 一時ファイルに書く → fsync → 直前の版を .bak1 に退避（世代ローテーション）→ アトミックに rename。
/// </summary>
public static class AtomicFileStore
{
    public const int MaxBackupGenerations = 20;

    public static void Write(string path, ReadOnlySpan<byte> content, int backupGenerations)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
                RotateBackups(fullPath, Math.Clamp(backupGenerations, 0, MaxBackupGenerations));

            // 同一ボリューム内の置換付き移動（Windows では MoveFileEx + REPLACE_EXISTING）
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>既存のバックアップを新しい順に返す（.bak1 が直前の版）。</summary>
    public static IReadOnlyList<string> ListBackups(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var list = new List<string>();
        for (var i = 1; i <= MaxBackupGenerations; i++)
        {
            var bak = BackupPath(fullPath, i);
            if (File.Exists(bak))
                list.Add(bak);
        }
        return list;
    }

    public static string BackupPath(string path, int generation) => $"{path}.bak{generation}";

    private static void RotateBackups(string fullPath, int generations)
    {
        // 押し出される最古の世代と、設定で世代数を減らした場合の余剰分を消す
        for (var i = Math.Max(generations, 1); i <= MaxBackupGenerations; i++)
        {
            var stale = BackupPath(fullPath, i);
            if (File.Exists(stale))
                File.Delete(stale);
        }

        if (generations == 0)
            return;

        for (var i = generations - 1; i >= 1; i--)
        {
            var src = BackupPath(fullPath, i);
            if (File.Exists(src))
                File.Move(src, BackupPath(fullPath, i + 1), overwrite: true);
        }

        File.Copy(fullPath, BackupPath(fullPath, 1), overwrite: true);
    }
}

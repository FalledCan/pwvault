using System.Security.Cryptography;

namespace PwVault.App.Services;

/// <summary>
/// この PC が作った「保存できなかった変更」の退避ファイルの記録（ファイルの場所と SHA-256）。
/// PC 内のデータフォルダに置き、同期フォルダには置かない。
/// マスターパスワードが変わって自動では取り込めなくなった退避ファイルだけを、前のパスワードを入れて取り込めるようにする。
/// 記録と中身が一致するものだけを対象にするので、同期フォルダに置かれた他人のファイルに古いパスワードを入れさせることはない。
/// </summary>
public sealed class RescueLog(string localDataDir)
{
    private string LogPath => Path.Combine(localDataDir, "Unsaved", "rescued.txt");

    /// <summary>退避したファイルを記録する。</summary>
    public void Add(string file)
    {
        try
        {
            var line = $"{Hash(file)}\t{Path.GetFullPath(file)}";
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllLines(LogPath, [line]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 記録できなくても退避ファイル自体は残る（同じパスワードなら自動で取り込まれる）
        }
    }

    /// <summary>まだ残っていて、記録したときと中身が同じ退避ファイル。消えた・変わったものは記録からも消す。</summary>
    public IReadOnlyList<string> Pending()
    {
        var kept = new List<(string Hash, string Path)>();
        foreach (var (hash, path) in Read())
        {
            try
            {
                if (File.Exists(path) && Hash(path) == hash) kept.Add((hash, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Write(kept);
        return kept.Select(k => k.Path).ToList();
    }

    /// <summary>取り込んだ（または不要になった）ファイルを記録から消す。</summary>
    public void Remove(string file)
    {
        var full = Path.GetFullPath(file);
        Write(Read().Where(r => !string.Equals(r.Path, full, StringComparison.OrdinalIgnoreCase)).ToList());
    }

    private List<(string Hash, string Path)> Read()
    {
        try
        {
            if (!File.Exists(LogPath)) return [];
            return File.ReadAllLines(LogPath)
                .Select(l => l.Split('\t'))
                .Where(p => p.Length == 2 && p[0].Length == 64 && p[1].Length > 0)
                .Select(p => (p[0], p[1]))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(List<(string Hash, string Path)> records)
    {
        try
        {
            if (records.Count == 0)
            {
                if (File.Exists(LogPath)) File.Delete(LogPath);
                return;
            }
            File.WriteAllLines(LogPath, records.Select(r => $"{r.Hash}\t{r.Path}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > Core.Vault.MaxFileBytes) return "";
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

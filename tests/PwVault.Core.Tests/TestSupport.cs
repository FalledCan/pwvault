using PwVault.Core.Crypto;
using PwVault.Core.Format;

namespace PwVault.Core.Tests;

/// <summary>テスト用の一時ディレクトリと、速く回るための軽い KDF パラメータ。</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pwvault-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class TestKdf
{
    /// <summary>読み込みで受け入れる最小値。実運用値ではない。</summary>
    public static KdfParameters Fast() => KdfParameters.CreateNew(KdfParameters.MinMemoryKiB, 1);
}

internal static class DocExtensions
{
    public static VaultDocument WithEntries(this VaultDocument doc, IEnumerable<EncryptedEntry> entries) =>
        doc with { Entries = entries.ToList() };

    public static EncryptedEntry With(this EncryptedEntry e, Guid? id = null, long? revision = null,
        string? updatedAt = null, bool? deleted = null, SealedBox? box = null) => new()
    {
        Id = id ?? e.Id,
        Revision = revision ?? e.Revision,
        UpdatedAt = updatedAt ?? e.UpdatedAt,
        Deleted = deleted ?? e.Deleted,
        Box = box ?? e.Box,
    };
}

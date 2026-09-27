using PwVault.Core.Crypto;

namespace PwVault.Core.Format;

/// <summary>保管庫ヘッダ（要件 6.1）。平文で保存するが、全体を AAD として認証対象に含める。</summary>
public sealed class VaultHeader
{
    public const string Magic = "PWV1";
    public const int CurrentFormatVersion = 1;
    public const string Cipher = "xchacha20poly1305";

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>
    /// 保管庫ごとの乱数 ID。エントリの AAD に含め、別の保管庫からエントリを移植されるのを検知する。
    /// フェーズ2では同期先の保管庫を識別する ID にもなる。
    /// </summary>
    public required Guid VaultId { get; init; }

    public required KdfParameters Kdf { get; init; }

    public required SealedBox WrappedVaultKey { get; init; }

    /// <summary>
    /// 保管庫鍵をラップするときの AAD。フォーマット版・KDF パラメータ・ソルト・暗号方式をすべて含むので、
    /// ヘッダのどこを書き換えてもアンラップが失敗する（SR-05, T2）。
    /// </summary>
    public static byte[] BuildWrapAad(int formatVersion, Guid vaultId, KdfParameters kdf) =>
        new AadBuilder()
            .String("pwvault/wrap")
            .String(Magic)
            .Int64(formatVersion)
            .Guid(vaultId)
            .String(KdfParameters.Algorithm)
            .Int64(kdf.MemoryKiB)
            .Int64(kdf.Iterations)
            .Int64(kdf.Parallelism)
            .Bytes(kdf.Salt)
            .String(Cipher)
            .ToArray();

    /// <summary>
    /// エントリの AAD。フォーマット版・保管庫 ID・エントリ ID と平文メタデータ（リビジョン・更新日時・削除フラグ）を含む。
    /// KDF パラメータは含めない。含めるとマスターパスワード変更のたびに全エントリの再暗号化が必要になり、
    /// 「変更時に再暗号化するのは保管庫鍵だけ」という設計（5.1）と矛盾するため。
    /// KDF パラメータの改ざんは、保管庫鍵のラップ AAD で検知する。
    /// </summary>
    public static byte[] BuildEntryAad(int formatVersion, Guid vaultId, Guid entryId, long revision, string updatedAt, bool deleted) =>
        new AadBuilder()
            .String("pwvault/entry")
            .String(Magic)
            .Int64(formatVersion)
            .Guid(vaultId)
            .String(Cipher)
            .Guid(entryId)
            .Int64(revision)
            .String(updatedAt)
            .Bool(deleted)
            .ToArray();

    public static byte[] BuildEntryAad(int formatVersion, Guid vaultId, EncryptedEntry entry) =>
        BuildEntryAad(formatVersion, vaultId, entry.Id, entry.Revision, entry.UpdatedAt, entry.Deleted);
}

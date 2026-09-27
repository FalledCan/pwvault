using System.Text.Json;
using NSec.Cryptography;
using PwVault.Core.Crypto;
using PwVault.Core.Format;
using PwVault.Core.Storage;

namespace PwVault.Core;

/// <summary>
/// アンロック済みの保管庫。保管庫鍵は NSec の Key（libsodium の保護メモリ）として保持し、
/// <see cref="Dispose"/>（＝ロック）で鍵を破棄し、復号済みデータへの参照を消す。
/// 復号済みの文字列は .NET の制約上ゼロ埋めできない（SR-08 の既知の限界）。
/// </summary>
public sealed class Vault : IDisposable
{
    public const int MaxHistoryItems = 20;

    private VaultHeader _header;
    private Key? _vaultKey;
    private readonly Dictionary<Guid, EncryptedEntry> _records;
    private readonly Dictionary<Guid, EntryData> _plain;
    private readonly TimeProvider _clock;

    public string FilePath { get; }

    /// <summary>未保存の変更があるか。</summary>
    public bool IsDirty { get; private set; }

    public Guid VaultId => _header.VaultId;
    public KdfParameters Kdf => _header.Kdf;

    private Vault(string path, VaultHeader header, Key vaultKey, IEnumerable<EncryptedEntry> records,
        Dictionary<Guid, EntryData> plain, TimeProvider clock)
    {
        FilePath = Path.GetFullPath(path);
        _header = header;
        _vaultKey = vaultKey;
        _records = records.ToDictionary(r => r.Id);
        _plain = plain;
        _clock = clock;
    }

    // ------------------------------------------------------------------ 作成・読み込み

    /// <summary>新しい保管庫を作ってファイルに保存する（FR-01）。</summary>
    public static Vault Create(string path, string masterPassword, KdfParameters kdf, int backupGenerations = 3,
        TimeProvider? clock = null)
    {
        if (File.Exists(path))
            throw new VaultException(VaultErrorKind.Io, "同じ名前のファイルが既にあります。");

        var vault = CreateInMemory(path, masterPassword, kdf, clock);
        try
        {
            vault.Save(backupGenerations);
            return vault;
        }
        catch
        {
            vault.Dispose();
            throw;
        }
    }

    internal static Vault CreateInMemory(string path, string masterPassword, KdfParameters kdf, TimeProvider? clock = null)
    {
        var vaultKey = KeyHierarchy.CreateVaultKey();
        try
        {
            var header = WrapVaultKey(Guid.NewGuid(), VaultHeader.CurrentFormatVersion, kdf, masterPassword, vaultKey);
            return new Vault(path, header, vaultKey, [], [], clock ?? TimeProvider.System) { IsDirty = true };
        }
        catch
        {
            vaultKey.Dispose();
            throw;
        }
    }

    /// <summary>保管庫ファイルを読み込んでアンロックする（FR-02）。</summary>
    public static Vault Open(string path, string masterPassword, TimeProvider? clock = null)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultErrorKind.Io, "保管庫ファイルを読み込めませんでした。", ex);
        }

        return Open(path, VaultFileCodec.Deserialize(bytes), masterPassword, clock);
    }

    internal static Vault Open(string path, VaultDocument doc, string masterPassword, TimeProvider? clock = null)
    {
        var vaultKey = UnwrapVaultKey(doc.Header, masterPassword)
            ?? throw new VaultException(VaultErrorKind.WrongPassword, "マスターパスワードが違います。");

        try
        {
            var plain = new Dictionary<Guid, EntryData>();
            foreach (var record in doc.Entries)
            {
                var data = DecryptEntry(doc.Header, vaultKey, record);
                if (!record.Deleted)
                    plain[record.Id] = data;
            }
            return new Vault(path, doc.Header, vaultKey, doc.Entries, plain, clock ?? TimeProvider.System);
        }
        catch
        {
            vaultKey.Dispose();
            throw;
        }
    }

    /// <summary>ファイルを開かずにパスワードだけ確かめる（平文エクスポート前の再認証など、SR-12）。</summary>
    public bool VerifyPassword(string masterPassword)
    {
        using var key = UnwrapVaultKey(_header, masterPassword);
        return key is not null;
    }

    // ------------------------------------------------------------------ エントリ操作

    /// <summary>ゴミ箱内を含む、墓標以外の全エントリ。</summary>
    public IReadOnlyList<VaultEntry> GetEntries()
    {
        EnsureUnlocked();
        return _plain.Select(kv => ToEntry(kv.Key, kv.Value)).ToList();
    }

    public VaultEntry? GetEntry(Guid id)
    {
        EnsureUnlocked();
        return _plain.TryGetValue(id, out var data) ? ToEntry(id, data) : null;
    }

    public Guid AddEntry(EntryData data)
    {
        EnsureUnlocked();
        var id = Guid.NewGuid();
        var copy = data.Clone();
        if (copy.CreatedAt == default)
            copy.CreatedAt = _clock.GetUtcNow();
        copy.History.Clear();
        Put(id, copy, revision: 1);
        return id;
    }

    /// <summary>エントリを更新する。パスワードが変わっていれば古い値を履歴に残す（FR-15）。</summary>
    public void UpdateEntry(Guid id, EntryData data)
    {
        EnsureUnlocked();
        var current = _plain.GetValueOrDefault(id) ?? throw new KeyNotFoundException("エントリが見つかりません。");

        var updated = data.Clone();
        updated.CreatedAt = current.CreatedAt;
        updated.History = current.History.Select(h => h with { }).ToList();
        if (current.Password.Length > 0 && current.Password != updated.Password)
        {
            updated.History.Insert(0, new PasswordHistoryItem(current.Password, _clock.GetUtcNow()));
            if (updated.History.Count > MaxHistoryItems)
                updated.History.RemoveRange(MaxHistoryItems, updated.History.Count - MaxHistoryItems);
        }

        Put(id, updated, _records[id].Revision + 1);
    }

    public void MoveToTrash(Guid id) => Mutate(id, d => d.TrashedAt = _clock.GetUtcNow());

    public void RestoreFromTrash(Guid id) => Mutate(id, d => d.TrashedAt = null);

    public void SetFavorite(Guid id, bool favorite) => Mutate(id, d => d.Favorite = favorite);

    /// <summary>
    /// 完全削除。中身を捨て、墓標（deleted=true）だけを残す（要件 6.3）。
    /// 墓標も空の平文を AEAD で暗号化し、メタデータ（ID・リビジョン・削除フラグ）を認証する。
    /// </summary>
    public void Purge(Guid id)
    {
        EnsureUnlocked();
        if (!_plain.Remove(id))
            throw new KeyNotFoundException("エントリが見つかりません。");

        _records[id] = Seal(id, _records[id].Revision + 1, deleted: true, []);
        IsDirty = true;
    }

    public void EmptyTrash()
    {
        foreach (var id in _plain.Where(kv => kv.Value.IsTrashed).Select(kv => kv.Key).ToList())
            Purge(id);
    }

    // ------------------------------------------------------------------ 保存・鍵の変更

    public void Save(int backupGenerations)
    {
        EnsureUnlocked();
        try
        {
            AtomicFileStore.Write(FilePath, ToBytes(), backupGenerations);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultErrorKind.Io, "保管庫を保存できませんでした。", ex);
        }
        IsDirty = false;
    }

    internal byte[] ToBytes() =>
        VaultFileCodec.Serialize(new VaultDocument(_header, _records.Values.OrderBy(r => r.Id).ToList()));

    /// <summary>
    /// マスターパスワードや KDF パラメータを変える（FR-10, FR-13）。
    /// 保管庫鍵を新しい KEK で包み直すだけで、エントリ本体は再暗号化しない。ソルトは毎回作り直す。
    /// </summary>
    public void ChangeMasterPassword(string currentPassword, string newPassword, int? memoryKiB = null, int? iterations = null)
    {
        EnsureUnlocked();
        if (!VerifyPassword(currentPassword))
            throw new VaultException(VaultErrorKind.WrongPassword, "現在のマスターパスワードが違います。");

        var kdf = KdfParameters.CreateNew(memoryKiB ?? _header.Kdf.MemoryKiB, iterations ?? _header.Kdf.Iterations);
        _header = WrapVaultKey(_header.VaultId, _header.FormatVersion, kdf, newPassword, _vaultKey!);
        IsDirty = true;
    }

    /// <summary>フェーズ2用: 同期サーバーへの認証キーを導出する。KEK とは別経路なので暗号化鍵は逆算できない。</summary>
    public SecretBuffer DeriveAuthKey(string masterPassword)
    {
        using var pw = SecretBuffer.FromPassword(masterPassword);
        using var masterKey = KeyHierarchy.DeriveMasterKey(pw.Span, _header.Kdf);
        return KeyHierarchy.DeriveAuthKey(masterKey.Span);
    }

    public bool IsLocked => _vaultKey is null;

    /// <summary>
    /// 保管庫本体以外の付随データ（アイコンのキャッシュなど）を保管庫鍵で暗号化する。
    /// AAD に用途名と保管庫 ID を入れるので、エントリや別の用途・別の保管庫のデータとは取り違えられない。
    /// </summary>
    public SealedBox SealAuxiliary(string purpose, ReadOnlySpan<byte> plaintext)
    {
        EnsureUnlocked();
        return AeadBox.Seal(_vaultKey!, AuxiliaryAad(purpose), plaintext);
    }

    /// <summary><see cref="SealAuxiliary"/> で暗号化したものを開く。認証に失敗したら null。</summary>
    public SecretBuffer? OpenAuxiliary(string purpose, SealedBox box)
    {
        EnsureUnlocked();
        return AeadBox.Open(_vaultKey!, AuxiliaryAad(purpose), box);
    }

    private byte[] AuxiliaryAad(string purpose) =>
        new AadBuilder().String("pwvault/aux").String(purpose).Guid(_header.VaultId).ToArray();

    /// <summary>ロック。保管庫鍵を破棄（libsodium がゼロ埋め）し、復号済みデータへの参照を消す。</summary>
    public void Dispose()
    {
        _vaultKey?.Dispose();
        _vaultKey = null;
        _plain.Clear();
        _records.Clear();
    }

    // ------------------------------------------------------------------ 内部

    private void Mutate(Guid id, Action<EntryData> change)
    {
        EnsureUnlocked();
        var data = (_plain.GetValueOrDefault(id) ?? throw new KeyNotFoundException("エントリが見つかりません。")).Clone();
        change(data);
        Put(id, data, _records[id].Revision + 1);
    }

    private void Put(Guid id, EntryData data, long revision)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(data, EntryJsonContext.Default.EntryData);
        try
        {
            _records[id] = Seal(id, revision, deleted: false, json);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(json);
        }

        _plain[id] = data;
        IsDirty = true;
    }

    /// <summary>リビジョンと更新日時を確定させ、それらを AAD に含めてエントリを暗号化する。</summary>
    private EncryptedEntry Seal(Guid id, long revision, bool deleted, ReadOnlySpan<byte> plaintext)
    {
        var updatedAt = EncryptedEntry.FormatTimestamp(_clock.GetUtcNow());
        var aad = VaultHeader.BuildEntryAad(_header.FormatVersion, _header.VaultId, id, revision, updatedAt, deleted);
        return new EncryptedEntry
        {
            Id = id,
            Revision = revision,
            UpdatedAt = updatedAt,
            Deleted = deleted,
            Box = AeadBox.Seal(_vaultKey!, aad, plaintext),
        };
    }

    private VaultEntry ToEntry(Guid id, EntryData data)
    {
        var record = _records[id];
        return new VaultEntry(id, record.Revision, record.UpdatedAtValue, data.Clone());
    }

    private void EnsureUnlocked()
    {
        if (_vaultKey is null)
            throw new InvalidOperationException("保管庫はロックされています。");
    }

    private static VaultHeader WrapVaultKey(Guid vaultId, int formatVersion, KdfParameters kdf, string password, Key vaultKey)
    {
        using var kek = KeyHierarchy.DeriveKekFromPassword(password, kdf);
        var raw = SecretBuffer.Allocate(vaultKey.GetExportBlobSize(KeyBlobFormat.RawSymmetricKey));
        using (raw)
        {
            if (!vaultKey.TryExport(KeyBlobFormat.RawSymmetricKey, raw.Span, out _))
                throw new InvalidOperationException("保管庫鍵を取り出せませんでした。");

            var aad = VaultHeader.BuildWrapAad(formatVersion, vaultId, kdf);
            return new VaultHeader
            {
                FormatVersion = formatVersion,
                VaultId = vaultId,
                Kdf = kdf,
                WrappedVaultKey = AeadBox.Seal(kek, aad, raw.Span),
            };
        }
    }

    private static Key? UnwrapVaultKey(VaultHeader header, string password)
    {
        using var kek = KeyHierarchy.DeriveKekFromPassword(password, header.Kdf);
        var aad = VaultHeader.BuildWrapAad(header.FormatVersion, header.VaultId, header.Kdf);
        using var raw = AeadBox.Open(kek, aad, header.WrappedVaultKey);
        return raw is null ? null : KeyHierarchy.ImportVaultKey(raw.Span);
    }

    private static EntryData DecryptEntry(VaultHeader header, Key vaultKey, EncryptedEntry record)
    {
        var aad = VaultHeader.BuildEntryAad(header.FormatVersion, header.VaultId, record);
        using var plaintext = AeadBox.Open(vaultKey, aad, record.Box)
            ?? throw new VaultException(VaultErrorKind.Tampered,
                "エントリの認証に失敗しました。保管庫ファイルが改ざんまたは破損しています。");

        if (record.Deleted)
            return new EntryData();

        try
        {
            return JsonSerializer.Deserialize(plaintext.Span, EntryJsonContext.Default.EntryData)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            // 例外メッセージに平文が混ざらないよう、元の例外は内側に含めない（SR-11）
            throw new VaultException(VaultErrorKind.InvalidFormat, "エントリの中身を読み取れませんでした。");
        }
    }
}

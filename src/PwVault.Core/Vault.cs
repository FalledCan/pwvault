using System.Security.Cryptography;
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

    public string FilePath { get; private set; }

    /// <summary>未保存の変更があるか。</summary>
    public bool IsDirty { get; private set; }

    public Guid VaultId => _header.VaultId;
    public KdfParameters Kdf => _header.Kdf;

    /// <summary>他の端末の変更を取り込んでメモリ上の内容が変わった回数（保存の直前の取り込みも含む。画面の更新に使う）。</summary>
    public int MergeCount { get; private set; }

    // 最後に読み書きしたときのファイルの中身の SHA-256 とヘッダ（他の端末が書き換えたかの判定と、ヘッダの取り込みに使う）
    private byte[]? _diskHash;
    private VaultHeader _diskHeader;

    private Vault(string path, VaultHeader header, Key vaultKey, IEnumerable<EncryptedEntry> records,
        Dictionary<Guid, EntryData> plain, TimeProvider clock, byte[]? diskHash = null)
    {
        FilePath = Path.GetFullPath(path);
        _header = header;
        _diskHeader = header;
        _diskHash = diskHash;
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

        return Open(path, VaultFileCodec.Deserialize(bytes), masterPassword, clock, SHA256.HashData(bytes));
    }

    internal static Vault Open(string path, VaultDocument doc, string masterPassword, TimeProvider? clock = null, byte[]? diskHash = null)
    {
        var vaultKey = UnwrapVaultKey(doc.Header, masterPassword)
            ?? throw new VaultException(VaultErrorKind.WrongPassword, "マスターパスワードが違います。");
        return OpenWithKey(path, doc, vaultKey, clock, diskHash);
    }

    // ------------------------------------------------------------------ クイックアンロック（Windows Hello など）

    /// <summary>
    /// マスターパスワード以外の鍵（Windows Hello から導出した鍵など）で保管庫鍵を包む。
    /// AAD に「現在のマスターパスワードで包んだ保管庫鍵」の指紋を入れるので、マスターパスワードを変えると自動的に無効になる。
    /// </summary>
    public SealedBox SealVaultKeyWith(ReadOnlySpan<byte> wrappingKey, string context)
    {
        EnsureUnlocked();
        using var key = Key.Import(KeyHierarchy.Aead, wrappingKey, KeyBlobFormat.RawSymmetricKey);
        using var raw = SecretBuffer.Allocate(_vaultKey!.GetExportBlobSize(KeyBlobFormat.RawSymmetricKey));
        if (!_vaultKey.TryExport(KeyBlobFormat.RawSymmetricKey, raw.Span, out _))
            throw new InvalidOperationException("保管庫鍵を取り出せませんでした。");
        return AeadBox.Seal(key, QuickUnlockAad(_header, context), raw.Span);
    }

    /// <summary><see cref="SealVaultKeyWith"/> で包んだ保管庫鍵を使ってアンロックする。鍵が違う・マスターパスワード変更後なら例外。</summary>
    public static Vault OpenWithWrappedKey(string path, ReadOnlySpan<byte> wrappingKey, string context, SealedBox wrappedVaultKey,
        TimeProvider? clock = null)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultErrorKind.Io, "保管庫ファイルを読み込めませんでした。", ex);
        }
        var doc = VaultFileCodec.Deserialize(bytes);

        using var key = Key.Import(KeyHierarchy.Aead, wrappingKey, KeyBlobFormat.RawSymmetricKey);
        using var raw = AeadBox.Open(key, QuickUnlockAad(doc.Header, context), wrappedVaultKey)
            ?? throw new VaultException(VaultErrorKind.QuickUnlockUnavailable,
                "登録が無効になっています（マスターパスワードの変更など）。マスターパスワードでアンロックしてください。");
        return OpenWithKey(path, doc, KeyHierarchy.ImportVaultKey(raw.Span), clock, SHA256.HashData(bytes));
    }

    private static byte[] QuickUnlockAad(VaultHeader header, string context) =>
        new AadBuilder()
            .String("pwvault/quick-unlock")
            .String(context)
            .Guid(header.VaultId)
            .Bytes(System.Security.Cryptography.SHA256.HashData(header.WrappedVaultKey.Ciphertext))
            .ToArray();

    private static Vault OpenWithKey(string path, VaultDocument doc, Key vaultKey, TimeProvider? clock, byte[]? diskHash)
    {
        try
        {
            var plain = new Dictionary<Guid, EntryData>();
            foreach (var record in doc.Entries)
            {
                var data = DecryptEntry(doc.Header, vaultKey, record);
                if (!record.Deleted)
                    plain[record.Id] = data;
            }
            return new Vault(path, doc.Header, vaultKey, doc.Entries, plain, clock ?? TimeProvider.System, diskHash);
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

    /// <summary>
    /// ファイルに保存する。同期フォルダで複数の端末から使うときのため、書く直前にファイルを読み直し、
    /// 他の端末の変更があれば取り込んでから書く（<see cref="SyncFromDisk"/>）。
    /// </summary>
    public void Save(int backupGenerations)
    {
        EnsureUnlocked();
        try
        {
            SyncFromDisk();
        }
        catch (VaultException ex) when (ex.Kind is VaultErrorKind.Corrupted or VaultErrorKind.InvalidFormat)
        {
            // 同期アプリの書き込み途中などで読めない。今のファイルは .bak に退避されるので、こちらの内容で書く
        }

        var bytes = ToBytes();
        try
        {
            AtomicFileStore.Write(FilePath, bytes, backupGenerations);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultErrorKind.Io, "保管庫を保存できませんでした。", ex);
        }
        _diskHash = SHA256.HashData(bytes);
        _diskHeader = _header;
        IsDirty = false;
    }

    /// <summary>
    /// 今の内容（暗号化済み）を別のファイルに書く。ファイルの読み直し・合体はしない。
    /// 保存できないままロックするときの退避用（変更をメモリごと捨てないため）。
    /// </summary>
    public void SaveCopyTo(string path)
    {
        EnsureUnlocked();
        try
        {
            AtomicFileStore.Write(path, ToBytes(), backupGenerations: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultErrorKind.Io, "保管庫を保存できませんでした。", ex);
        }
    }

    // ------------------------------------------------------------------ 複数の端末での利用（同期フォルダ）

    /// <summary>
    /// ファイルが他の端末で書き換えられていたら、エントリ単位で取り込む。メモリ上の内容が変わったら true。
    /// こちらにしかない変更が残っていれば <see cref="IsDirty"/> を立てる（保存すると書き戻される）。
    /// ファイルが無いときは何もしない。
    /// </summary>
    public bool SyncFromDisk()
    {
        EnsureUnlocked();
        byte[] bytes;
        try
        {
            if (!File.Exists(FilePath)) return false;
            bytes = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VaultException(VaultErrorKind.Io, "保管庫ファイルを読み込めませんでした。", ex);
        }

        var hash = SHA256.HashData(bytes);
        if (_diskHash is not null && hash.AsSpan().SequenceEqual(_diskHash))
            return false;

        var doc = VaultFileCodec.Deserialize(bytes);
        var changed = Merge(doc, adoptHeader: true);
        _diskHash = hash;
        _diskHeader = doc.Header;
        if (!ToBytes().AsSpan().SequenceEqual(bytes))
            IsDirty = true;
        return changed;
    }

    /// <summary>
    /// 同期アプリが作った競合コピー（「vault (conflicted copy …).pwv」「vault (1).pwv」など）のうち、
    /// この保管庫のものを取り込む。取り込んだコピーのパスを返す（保存に成功したら消してよい）。
    /// </summary>
    public IReadOnlyList<string> MergeConflictCopies()
    {
        EnsureUnlocked();
        var merged = new List<string>();
        foreach (var path in FindConflictCopies(FilePath))
        {
            try
            {
                var doc = VaultFileCodec.Deserialize(File.ReadAllBytes(path));
                if (doc.Header.VaultId != VaultId) continue;
                Merge(doc, adoptHeader: false);
                merged.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultException)
            {
                // 読めない・壊れているコピーは取り込まずに残す
            }
        }
        if (merged.Count > 0) IsDirty = true;
        return merged;
    }

    /// <summary>同期アプリの競合コピーらしいファイル（同じフォルダ・同じ拡張子で、名前が「元の名前 (…)」や「…conflict…」）。</summary>
    public static IReadOnlyList<string> FindConflictCopies(string vaultPath)
    {
        var full = Path.GetFullPath(vaultPath);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        var ext = Path.GetExtension(full);
        if (!Directory.Exists(dir)) return [];

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return Directory.EnumerateFiles(dir)
            .Where(p => string.Equals(Path.GetExtension(p), ext, comparison) && !string.Equals(p, full, comparison))
            .Where(p =>
            {
                var n = Path.GetFileNameWithoutExtension(p);
                if (!n.StartsWith(name, comparison)) return false;
                var rest = n[name.Length..];
                return rest.StartsWith(" (", StringComparison.Ordinal) && rest.EndsWith(')')
                       || rest.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                       || rest.Contains("競合", StringComparison.Ordinal);
            })
            .ToList();
    }

    /// <summary>
    /// 保管庫を別の場所に保存し直し、以後はそこを使う（保存先の切り替え）。元のファイルは消さない。
    /// 移動先に同じ保管庫があれば合体する。別の保管庫があれば <see cref="VaultErrorKind.DifferentVault"/>。
    /// </summary>
    public void MoveTo(string newPath, int backupGenerations)
    {
        EnsureUnlocked();
        var (oldPath, oldHash, oldHeader) = (FilePath, _diskHash, _diskHeader);
        FilePath = Path.GetFullPath(newPath);
        _diskHash = null;
        _diskHeader = _header;
        try
        {
            Save(backupGenerations);
        }
        catch
        {
            (FilePath, _diskHash, _diskHeader) = (oldPath, oldHash, oldHeader);
            throw;
        }
    }

    /// <summary>
    /// 他の端末のファイルの内容を取り込む。エントリごとに「リビジョンが大きい方 → 更新日時が新しい方」を残す。
    /// 両方が同じ版から別々に変えていた（リビジョンが同じ）ときは、負けた側のパスワードを勝った側の履歴に残す。
    /// 取り込むエントリはすべて保管庫鍵で認証してから反映する（1 つでも失敗したら何も反映しない）。
    /// </summary>
    private bool Merge(VaultDocument doc, bool adoptHeader)
    {
        EnsureUnlocked();
        if (doc.Header.VaultId != _header.VaultId)
            throw new VaultException(VaultErrorKind.DifferentVault, "別の保管庫のファイルです。");

        var incoming = doc.Entries.Select(r => (Record: r, Data: DecryptEntry(doc.Header, _vaultKey!, r))).ToList();

        var changed = false;
        // 他の端末でマスターパスワードや KDF を変えていたら、こちらも合わせる（こちらでも変えていたらこちらを優先）
        if (adoptHeader && !SameHeader(doc.Header, _header) && SameHeader(_header, _diskHeader))
        {
            _header = doc.Header;
            changed = true;
        }

        foreach (var (theirs, theirData) in incoming)
        {
            if (!_records.TryGetValue(theirs.Id, out var mine))
            {
                Accept(theirs, theirData);
                changed = true;
                continue;
            }
            if (SameRecord(mine, theirs)) continue;

            var conflict = mine.Revision == theirs.Revision;
            if (CompareRecords(theirs, mine) > 0)
            {
                var myPassword = mine.Deleted ? null : _plain.GetValueOrDefault(mine.Id)?.Password;
                Accept(theirs, theirData);
                changed = true;
                if (conflict && myPassword is not null) KeepLosingPassword(theirs.Id, myPassword);
            }
            else if (conflict && !theirs.Deleted)
            {
                KeepLosingPassword(mine.Id, theirData.Password);
            }
        }
        if (changed) MergeCount++;
        return changed;
    }

    private void Accept(EncryptedEntry record, EntryData data)
    {
        _records[record.Id] = record;
        if (record.Deleted) _plain.Remove(record.Id);
        else _plain[record.Id] = data;
    }

    /// <summary>競合で負けた側のパスワードを、残した側の履歴に入れる（どちらかの端末で入れたパスワードを失わないように）。</summary>
    private void KeepLosingPassword(Guid id, string password)
    {
        if (password.Length == 0 || !_plain.TryGetValue(id, out var data)) return;
        if (data.Password == password || data.History.Any(h => h.Password == password)) return;

        var updated = data.Clone();
        updated.History.Insert(0, new PasswordHistoryItem(password, _clock.GetUtcNow()));
        if (updated.History.Count > MaxHistoryItems)
            updated.History.RemoveRange(MaxHistoryItems, updated.History.Count - MaxHistoryItems);
        Put(id, updated, _records[id].Revision + 1);
    }

    /// <summary>どちらのエントリを残すか。どの端末で比べても同じ結果になるよう、最後は暗号文のバイト列で決める。</summary>
    private static int CompareRecords(EncryptedEntry a, EncryptedEntry b)
    {
        var c = a.Revision.CompareTo(b.Revision);
        if (c == 0) c = string.CompareOrdinal(a.UpdatedAt, b.UpdatedAt); // 固定書式なので文字列順 = 時刻順
        if (c == 0) c = a.Deleted.CompareTo(b.Deleted);
        if (c == 0) c = a.Box.Ciphertext.AsSpan().SequenceCompareTo(b.Box.Ciphertext);
        if (c == 0) c = a.Box.Nonce.AsSpan().SequenceCompareTo(b.Box.Nonce);
        return c;
    }

    private static bool SameRecord(EncryptedEntry a, EncryptedEntry b) =>
        a.Revision == b.Revision && a.UpdatedAt == b.UpdatedAt && a.Deleted == b.Deleted
        && a.Box.Nonce.AsSpan().SequenceEqual(b.Box.Nonce) && a.Box.Ciphertext.AsSpan().SequenceEqual(b.Box.Ciphertext);

    private static bool SameHeader(VaultHeader a, VaultHeader b) =>
        a.VaultId == b.VaultId && a.FormatVersion == b.FormatVersion
        && a.WrappedVaultKey.Nonce.AsSpan().SequenceEqual(b.WrappedVaultKey.Nonce)
        && a.WrappedVaultKey.Ciphertext.AsSpan().SequenceEqual(b.WrappedVaultKey.Ciphertext);

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

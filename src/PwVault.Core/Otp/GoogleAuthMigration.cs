namespace PwVault.Core.Otp;

/// <summary>移行用 QR コード 1 枚の中身。</summary>
/// <param name="Keys">取り込めたキー（時刻で変わる方式のもの）。</param>
/// <param name="Unsupported">取り込めなかった件数（回数で変わる方式・MD5 など）。</param>
/// <param name="BatchSize">QR コードが全部で何枚か（1 以上）。</param>
/// <param name="BatchIndex">この QR コードが何枚目か（0 から）。</param>
/// <param name="BatchId">同じ書き出しの QR コードに共通の番号。</param>
public sealed record MigrationBatch(IReadOnlyList<TotpKey> Keys, int Unsupported, int BatchSize, int BatchIndex, int BatchId);

/// <summary>
/// Google Authenticator の「アカウントを移行 → エクスポート」で出る QR コード（otpauth-migration://offline?data=...）を読む。
/// 中身は Protocol Buffers（MigrationPayload）を Base64 にしたもの。必要な項目だけを読む小さな読み取り器を持つ。
/// </summary>
public static class GoogleAuthMigration
{
    public const string Scheme = "otpauth-migration:";

    public static bool IsMigrationUri(string? text) =>
        text?.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryParse(string? text, out MigrationBatch? batch, out string? error)
    {
        batch = null;
        error = "Google Authenticator の移行用 QR コードではありません。";
        if (!IsMigrationUri(text) || text!.Length > 64 * 1024) return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return false;

        string? data = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
            if (part.StartsWith("data=", StringComparison.OrdinalIgnoreCase))
                data = Uri.UnescapeDataString(part[5..]);
        if (data is null) return false;

        byte[] payload;
        try
        {
            // 標準の Base64。URL 用の文字（- _）や、途中の空白・改行が混じっていても読む
            var normalized = data.Replace(' ', '+').Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            payload = Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            error = "移行用 QR コードの中身を読めませんでした。";
            return false;
        }

        try
        {
            batch = ReadPayload(payload);
            error = null;
            return true;
        }
        catch (FormatException)
        {
            error = "移行用 QR コードの中身を読めませんでした。";
            return false;
        }
    }

    private static MigrationBatch ReadPayload(ReadOnlySpan<byte> data)
    {
        var keys = new List<TotpKey>();
        int unsupported = 0, batchSize = 1, batchIndex = 0, batchId = 0;
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field, wire)
            {
                case (1, 2):
                    if (ReadOtp(reader.Bytes()) is { } key) keys.Add(key);
                    else unsupported++;
                    break;
                case (3, 0): batchSize = (int)Math.Clamp(reader.Varint(), 1, 1000); break;
                case (4, 0): batchIndex = (int)Math.Clamp(reader.Varint(), 0, 1000); break;
                case (5, 0): batchId = unchecked((int)reader.Varint()); break;
                default: reader.Skip(wire); break;
            }
        }
        return new MigrationBatch(keys, unsupported, batchSize, batchIndex, batchId);
    }

    /// <summary>OtpParameters 1 件。対応していない方式なら null。</summary>
    private static TotpKey? ReadOtp(ReadOnlySpan<byte> data)
    {
        byte[] secret = [];
        string name = "", issuer = "";
        ulong algorithm = 0, digits = 0, type = 0;
        var reader = new ProtoReader(data);
        while (reader.Next(out var field, out var wire))
        {
            switch (field, wire)
            {
                case (1, 2): secret = reader.Bytes().ToArray(); break;
                case (2, 2): name = System.Text.Encoding.UTF8.GetString(reader.Bytes()); break;
                case (3, 2): issuer = System.Text.Encoding.UTF8.GetString(reader.Bytes()); break;
                case (4, 0): algorithm = reader.Varint(); break;
                case (5, 0): digits = reader.Varint(); break;
                case (6, 0): type = reader.Varint(); break;
                default: reader.Skip(wire); break;
            }
        }

        // type: 0=不明（TOTP として扱う）, 1=HOTP, 2=TOTP / algorithm: 0,1=SHA1, 2=SHA256, 3=SHA512, 4=MD5 / digits: 0,1=6 桁, 2=8 桁
        if (type == 1 || secret.Length is < TotpKey.MinSecretBytes or > TotpKey.MaxSecretBytes) return null;
        OtpAlgorithm? alg = algorithm switch
        {
            0 or 1 => OtpAlgorithm.Sha1,
            2 => OtpAlgorithm.Sha256,
            3 => OtpAlgorithm.Sha512,
            _ => null,
        };
        int? digitCount = digits switch { 0 or 1 => 6, 2 => 8, _ => null };
        if (alg is null || digitCount is null) return null;

        // name は「発行元:アカウント」のこともある
        var colon = name.IndexOf(':');
        var account = name;
        if (colon >= 0)
        {
            if (issuer.Length == 0) issuer = name[..colon].Trim();
            account = name[(colon + 1)..];
        }
        return new TotpKey(secret, alg.Value, digitCount.Value, 30, issuer.Trim(), account.Trim());
    }

    /// <summary>Protocol Buffers の最小限の読み取り（varint と長さ付きの項目だけ使う）。</summary>
    private ref struct ProtoReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public bool Next(out int field, out int wire)
        {
            field = wire = 0;
            if (_pos >= _data.Length) return false;
            var tag = Varint();
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            if (field == 0) throw new FormatException();
            return true;
        }

        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_pos >= _data.Length) throw new FormatException();
                var b = _data[_pos++];
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return value;
            }
            throw new FormatException();
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var length = Varint();
            if (length > (ulong)(_data.Length - _pos)) throw new FormatException();
            var slice = _data.Slice(_pos, (int)length);
            _pos += (int)length;
            return slice;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: Advance(8); break;
                case 2: Bytes(); break;
                case 5: Advance(4); break;
                default: throw new FormatException();
            }
        }

        private void Advance(int n)
        {
            if (_data.Length - _pos < n) throw new FormatException();
            _pos += n;
        }
    }
}

/// <summary>取り込むキーを、既にあるエントリに結び付ける候補を探す。</summary>
public static class TotpMatcher
{
    /// <summary>
    /// 発行元・アカウント名から、いちばん合いそうなエントリを返す（無ければ null）。
    /// 発行元がタイトルか URL のホストに含まれ、アカウント名がユーザー ID と同じものを優先する。
    /// </summary>
    public static VaultEntry? Suggest(IEnumerable<VaultEntry> entries, TotpKey key)
    {
        var issuer = Simplify(key.Issuer);
        var account = key.Account.Trim();
        VaultEntry? best = null;
        var bestScore = 0;
        foreach (var e in entries.Where(e => !e.Data.IsTrashed))
        {
            var score = 0;
            if (issuer.Length >= 2)
            {
                var title = Simplify(e.Data.Title);
                var host = Simplify(Bridge.UrlMatcher.HostKey(e.Data.Url) ?? "");
                if (title == issuer) score += 3;
                else if (title.Contains(issuer, StringComparison.Ordinal) || (title.Length >= 2 && issuer.Contains(title, StringComparison.Ordinal))) score += 2;
                if (host.Contains(issuer, StringComparison.Ordinal)) score += 2;
                // 同じメールアドレスは多くのサイトで使うので、発行元が合わなければアカウントだけでは選ばない
                if (score == 0) continue;
            }
            if (account.Length > 0 && e.Data.Username.Trim().Equals(account, StringComparison.OrdinalIgnoreCase)) score += 3;
            if (score > bestScore && score >= 2)
            {
                best = e;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>比べやすい形（小文字、空白・記号を除く）。</summary>
    private static string Simplify(string text) =>
        new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

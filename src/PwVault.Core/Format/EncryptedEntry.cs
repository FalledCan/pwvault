using System.Globalization;
using PwVault.Core.Crypto;

namespace PwVault.Core.Format;

/// <summary>
/// ファイル上のエントリ。ID・リビジョン・更新日時・削除フラグだけが平文（同期用）で、
/// タイトルや URL を含む中身はすべて暗号文に入る（要件 6.1）。
/// </summary>
public sealed class EncryptedEntry
{
    public required Guid Id { get; init; }
    public required long Revision { get; init; }

    /// <summary>UTC・ミリ秒精度の固定書式。AAD にはこの文字列をそのまま使う。</summary>
    public required string UpdatedAt { get; init; }

    /// <summary>完全削除の墓標。true のとき暗号文の中身は空で、メタデータの認証のためだけに存在する。</summary>
    public required bool Deleted { get; init; }

    public required SealedBox Box { get; init; }

    public DateTimeOffset UpdatedAtValue =>
        DateTimeOffset.ParseExact(UpdatedAt, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static bool IsValidTimestamp(string? value) =>
        value is not null
        && DateTimeOffset.TryParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
}

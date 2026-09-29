namespace PwVault.Core;

/// <summary>保管庫の読み書きで発生したエラーの種類。UI はこれを見て案内を出し分ける（NFR-06）。</summary>
public enum VaultErrorKind
{
    /// <summary>マスターパスワードが違う（保管庫鍵のアンラップに失敗）。</summary>
    WrongPassword,
    /// <summary>ファイルの構造が壊れている、または保管庫ファイルではない。</summary>
    InvalidFormat,
    /// <summary>チェックサム不一致。書き込み途中の破損など偶発的な破損。</summary>
    Corrupted,
    /// <summary>ヘッダは正しいがエントリの認証に失敗した。改ざんまたは破損。</summary>
    Tampered,
    /// <summary>このアプリより新しいフォーマット。</summary>
    UnsupportedVersion,
    /// <summary>ファイル入出力の失敗。</summary>
    Io,
    /// <summary>Windows Hello などのクイックアンロックが使えない（期限切れ・マスターパスワード変更後など）。</summary>
    QuickUnlockUnavailable,
    /// <summary>取り込もうとしたファイルが別の保管庫（保管庫 ID が違う）。</summary>
    DifferentVault,
}

/// <summary>
/// 保管庫操作の例外。メッセージには機密値（パスワード・鍵・復号データ）を決して含めない（SR-11）。
/// </summary>
public sealed class VaultException : Exception
{
    public VaultErrorKind Kind { get; }

    public VaultException(VaultErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>バックアップからの復元を案内すべきエラーか。</summary>
    public bool SuggestsBackupRestore =>
        Kind is VaultErrorKind.InvalidFormat or VaultErrorKind.Corrupted or VaultErrorKind.Tampered;
}

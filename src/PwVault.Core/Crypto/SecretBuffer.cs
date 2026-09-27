using System.Security.Cryptography;
using System.Text;

namespace PwVault.Core.Crypto;

/// <summary>
/// GC に移動されないようピン留めしたバイト配列。Dispose でゼロ埋めする（SR-08）。
/// 注意: .NET の string は消去できないため、呼び出し元が持つ string のパスワードは残りうる。
/// </summary>
public sealed class SecretBuffer : IDisposable
{
    private byte[]? _data;

    private SecretBuffer(byte[] data) => _data = data;

    public static SecretBuffer Allocate(int length) =>
        new(GC.AllocateArray<byte>(length, pinned: true));

    /// <summary>パスワードを NFC 正規化した UTF-8 バイト列にする。端末や OS をまたいでも同じ鍵になるように正規化する。</summary>
    public static SecretBuffer FromPassword(string password)
    {
        var normalized = password.Normalize(NormalizationForm.FormC);
        var buffer = Allocate(Encoding.UTF8.GetByteCount(normalized));
        Encoding.UTF8.GetBytes(normalized, buffer.Span);
        return buffer;
    }

    public Span<byte> Span => _data ?? throw new ObjectDisposedException(nameof(SecretBuffer));

    public int Length => Span.Length;

    public void Dispose()
    {
        if (_data is null) return;
        CryptographicOperations.ZeroMemory(_data);
        _data = null;
    }
}

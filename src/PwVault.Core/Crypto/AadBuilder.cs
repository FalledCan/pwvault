using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace PwVault.Core.Crypto;

/// <summary>
/// AAD を曖昧さのないバイト列として組み立てる。各フィールドは「4 バイト長（ビッグエンディアン）＋中身」。
/// 区切り文字ではなく長さを前置するので、フィールド境界をずらした別解釈が起きない。
/// </summary>
internal sealed class AadBuilder
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public AadBuilder Bytes(ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.GetSpan(4), (uint)value.Length);
        _buffer.Advance(4);
        _buffer.Write(value);
        return this;
    }

    public AadBuilder String(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    public AadBuilder Int64(long value)
    {
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(tmp, value);
        return Bytes(tmp);
    }

    public AadBuilder Bool(bool value) => Bytes([value ? (byte)1 : (byte)0]);

    public AadBuilder Guid(Guid value) => Bytes(value.ToByteArray(bigEndian: true));

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();
}

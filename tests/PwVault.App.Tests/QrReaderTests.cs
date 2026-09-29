using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using PwVault.App.Services;
using SkiaSharp;

namespace PwVault.App.Tests;

/// <summary>画像からの QR の読み取り（細工した画像への耐性）。</summary>
public class QrReaderTests
{
    [Fact]
    public void HugeDimensionsInSmallFile_IsRejected_WithoutDecoding()
    {
        var dir = Directory.CreateTempSubdirectory("pwvault-qr").FullName;
        try
        {
            // 1 MB 未満のファイルだが、展開すると 30000×30000 画素（BGRA で 3.6 GB）になる PNG
            var path = Path.Combine(dir, "bomb.png");
            File.WriteAllBytes(path, GrayPng(30000, 30000));
            Assert.True(new FileInfo(path).Length < QrReader.MaxImageBytes);

            Assert.Null(QrReader.DecodeFile(path, out var error));
            Assert.Contains("大きすぎ", error);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LargePhoto_IsScaledDown_AndStillRead()
    {
        var dir = Directory.CreateTempSubdirectory("pwvault-qr").FullName;
        try
        {
            // 8000×6000（4800 万画素）の写真の真ん中に QR コード
            var qr = TestQr.Frame("otpauth://totp/Big:alice?secret=JBSWY3DPEHPK3PXP", 360);
            using var small = new SKBitmap(new SKImageInfo(qr.Width, qr.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(qr.Bgra, 0, small.GetPixels(), qr.Bgra.Length);
            using var photo = new SKBitmap(8000, 6000);
            using (var canvas = new SKCanvas(photo))
            {
                canvas.Clear(SKColors.White);
                using var image = SKImage.FromBitmap(small);
                canvas.DrawImage(image, new SKRect(1000, 0, 7000, 6000), new SKSamplingOptions(SKFilterMode.Nearest));
            }
            var path = Path.Combine(dir, "photo.jpg");
            using (var data = photo.Encode(SKEncodedImageFormat.Jpeg, 90)) File.WriteAllBytes(path, data.ToArray());

            Assert.Equal("otpauth://totp/Big:alice?secret=JBSWY3DPEHPK3PXP", QrReader.DecodeFile(path, out var error));
            Assert.Null(error);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>全部 0（黒）のグレースケール PNG。中身は圧縮でほとんど消えるので、ファイルは小さい。</summary>
    private static byte[] GrayPng(int width, int height)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // 8 ビット、グレースケール
        var pixels = new MemoryStream();
        using (var z = new ZLibStream(pixels, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[width + 1];
            for (var y = 0; y < height; y++) z.Write(row);
        }
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", header), .. Chunk("IDAT", pixels.ToArray()), .. Chunk("IEND", [])];
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] body = [.. Encoding.ASCII.GetBytes(type), .. data];
        var result = new byte[body.Length + 8];
        BinaryPrimitives.WriteInt32BigEndian(result, data.Length);
        body.CopyTo(result, 4);
        uint crc = 0xffffffff;
        foreach (var b in body)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(body.Length + 4), ~crc);
        return result;
    }
}

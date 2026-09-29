using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace PwVault.App.Services;

/// <summary>QR コードの読み取り（カメラの 1 コマ・画像ファイル）。PC の中だけで行い、通信はしない。</summary>
public static class QrReader
{
    /// <summary>画像ファイルの大きさの上限（スクリーンショット・写真には十分）。</summary>
    public const long MaxImageBytes = 30 * 1024 * 1024;

    /// <summary>これより大きい画像は縮めてから読む（読み取りの時間とメモリを抑える）。</summary>
    private const int MaxSide = 2400;

    /// <summary>展開する画素数の上限（約 5000 万画素 = 200 MB。スマホの写真はこれに収まる）。</summary>
    public const long MaxDecodePixels = 50_000_000;

    private static readonly DecodingOptions Options = new()
    {
        PossibleFormats = [BarcodeFormat.QR_CODE],
        TryHarder = true,
        TryInverted = true, // ダークモードの画面（白黒が逆）でも読む
    };

    /// <summary>BGRA（1 画素 4 バイト、行の詰め物なし）の画像から読む。見つからなければ null。</summary>
    public static string? Decode(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var reader = new BarcodeReaderGeneric { Options = Options, AutoRotate = true };
        return reader.Decode(source)?.Text;
    }

    /// <summary>画像ファイルから読む。読めなければ error に理由。</summary>
    public static string? DecodeFile(string path, out string? error)
    {
        error = null;
        try
        {
            if (new FileInfo(path).Length > MaxImageBytes)
            {
                error = "画像が大きすぎます。";
                return null;
            }
            // 先に縦横の大きさだけ見る。ファイルが小さくても、展開すると巨大になる画像（細工した PNG など）で
            // メモリを使い果たさないように、展開後の画素数に上限を設ける（JPEG は読み込み時に縮められる）
            using var codec = SKCodec.Create(path);
            if (codec is null)
            {
                error = "画像として読めませんでした（PNG・JPEG のスクリーンショットを選んでください）。";
                return null;
            }
            var size = codec.GetScaledDimensions(Math.Min(1f, (float)MaxSide / Math.Max(1, Math.Max(codec.Info.Width, codec.Info.Height))));
            if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaxDecodePixels)
            {
                error = "画像が大きすぎます（縦横の画素数が多すぎます）。";
                return null;
            }
            using var original = SKBitmap.Decode(codec, new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (original is null)
            {
                error = "画像として読めませんでした（PNG・JPEG のスクリーンショットを選んでください）。";
                return null;
            }

            var scale = Math.Min(1.0, (double)MaxSide / Math.Max(original.Width, original.Height));
            var info = new SKImageInfo(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale)),
                SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            if (!original.ScalePixels(bitmap, new SKSamplingOptions(SKFilterMode.Linear)))
            {
                error = "画像を読めませんでした。";
                return null;
            }
            var text = Decode(bitmap.Bytes, bitmap.Width, bitmap.Height);
            if (text is null) error = "画像の中に QR コードが見つかりませんでした。";
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "ファイルを読み込めませんでした。";
            return null;
        }
    }
}

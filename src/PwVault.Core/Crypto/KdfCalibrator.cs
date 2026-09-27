using System.Diagnostics;
using System.Security.Cryptography;

namespace PwVault.Core.Crypto;

/// <summary>
/// この端末でアンロックが目標時間（既定 0.75 秒）になる Argon2id のパラメータを求める（SR-01, NFR-02）。
/// Argon2 は使用メモリ量が総当たり耐性（GPU・専用回路のコスト）に最も効くので、
/// まず反復 3 回で目標に収まる範囲でメモリを 64 MiB から倍々に増やし（上限 256 MiB）、残りを反復回数で合わせる。
/// </summary>
public static class KdfCalibrator
{
    public const int DefaultMinMemoryKiB = 64 * 1024;
    public const int DefaultMaxMemoryKiB = 256 * 1024;
    public const int MaxCalibratedIterations = 32;
    private const int PreferredMinIterations = 3;
    public static readonly TimeSpan DefaultTarget = TimeSpan.FromMilliseconds(750);

    public static KdfParameters Calibrate(int minMemoryKiB = DefaultMinMemoryKiB, int maxMemoryKiB = DefaultMaxMemoryKiB,
        TimeSpan? target = null)
    {
        var goalMs = (target ?? DefaultTarget).TotalMilliseconds;

        // 1 回目はウォームアップ（ページ確保などの初回コストを除く）
        Measure(minMemoryKiB, 1);

        var memory = minMemoryKiB;
        var perPassMs = MeasurePerPassMs(memory);
        while (memory * 2L <= maxMemoryKiB && perPassMs * 2 * PreferredMinIterations <= goalMs)
        {
            memory *= 2;
            perPassMs = MeasurePerPassMs(memory);
        }

        var iterations = (int)Math.Round(goalMs / Math.Max(perPassMs, 1));
        iterations = Math.Clamp(iterations, KdfParameters.MinRecommendedIterations, MaxCalibratedIterations);
        return KdfParameters.CreateNew(memory, iterations);
    }

    /// <summary>指定パラメータでの導出時間を実測する（設定画面での確認用）。</summary>
    public static TimeSpan Measure(int memoryKiB, int iterations)
    {
        var kdf = KdfParameters.CreateNew(memoryKiB, iterations);
        var dummyPassword = RandomNumberGenerator.GetBytes(16);
        var sw = Stopwatch.StartNew();
        using (KeyHierarchy.DeriveMasterKey(dummyPassword, kdf)) { }
        return sw.Elapsed;
    }

    private static double MeasurePerPassMs(int memoryKiB)
    {
        const int probeIterations = 2;
        return Measure(memoryKiB, probeIterations).TotalMilliseconds / probeIterations;
    }
}

using System.Diagnostics;
using System.Security.Cryptography;

namespace PwVault.Core.Crypto;

/// <summary>
/// この端末でアンロックが目標時間（既定 0.75 秒）になる Argon2id の反復回数を求める（SR-01, NFR-02）。
/// メモリ量は固定し、反復回数で調整する。
/// </summary>
public static class KdfCalibrator
{
    public const int DefaultMemoryKiB = 64 * 1024;
    public static readonly TimeSpan DefaultTarget = TimeSpan.FromMilliseconds(750);

    public static KdfParameters Calibrate(int memoryKiB = DefaultMemoryKiB, TimeSpan? target = null)
    {
        var goal = target ?? DefaultTarget;
        const int probeIterations = 2;

        // 1 回目はウォームアップ（ページ確保などの初回コストを除く）
        Measure(memoryKiB, 1);
        var elapsed = Measure(memoryKiB, probeIterations);

        var perPass = elapsed.TotalMilliseconds / probeIterations;
        var iterations = (int)Math.Round(goal.TotalMilliseconds / Math.Max(perPass, 1));
        iterations = Math.Clamp(iterations, KdfParameters.MinRecommendedIterations, 32);

        return KdfParameters.CreateNew(memoryKiB, iterations);
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
}

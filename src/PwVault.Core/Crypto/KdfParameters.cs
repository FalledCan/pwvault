using System.Security.Cryptography;

namespace PwVault.Core.Crypto;

/// <summary>
/// Argon2id のパラメータ。保管庫ヘッダに平文で保存し、後から引き上げられるようにする（SR-01）。
/// </summary>
public sealed class KdfParameters
{
    public const string Algorithm = "argon2id";
    public const int SaltSize = 16;

    // 読み込み時に受け入れる範囲。UI から設定できる範囲はもっと狭い（MinRecommended*）。
    public const int MinMemoryKiB = 19 * 1024;
    public const int MaxMemoryKiB = 4 * 1024 * 1024;
    public const int MinIterations = 1;
    public const int MaxIterations = 64;

    public const int MinRecommendedMemoryKiB = 64 * 1024;
    public const int MinRecommendedIterations = 2;

    public int MemoryKiB { get; }
    public int Iterations { get; }

    /// <summary>libsodium の Argon2id は並列度 1 のみ対応。将来の実装変更に備えてヘッダには保存する。</summary>
    public int Parallelism { get; }

    public byte[] Salt { get; }

    public KdfParameters(int memoryKiB, int iterations, int parallelism, byte[] salt)
    {
        MemoryKiB = memoryKiB;
        Iterations = iterations;
        Parallelism = parallelism;
        Salt = salt;
    }

    /// <summary>OS の CSPRNG で新しいソルトを生成してパラメータを作る（SR-02, SR-06）。</summary>
    public static KdfParameters CreateNew(int memoryKiB, int iterations) =>
        new(memoryKiB, iterations, 1, RandomNumberGenerator.GetBytes(SaltSize));

    /// <summary>同じコストで新しいソルトを持つパラメータを返す（マスターパスワード変更時に使う）。</summary>
    public KdfParameters WithNewSalt() => CreateNew(MemoryKiB, Iterations);

    public bool IsValid =>
        MemoryKiB is >= MinMemoryKiB and <= MaxMemoryKiB
        && Iterations is >= MinIterations and <= MaxIterations
        && Parallelism == 1
        && Salt is { Length: SaltSize };
}

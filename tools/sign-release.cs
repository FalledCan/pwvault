#:project ../src/PwVault.Core/PwVault.Core.csproj
// リリースファイルに署名する（GitHub Actions のリリース用ワークフローから使う）。
//   dotnet run tools/sign-release.cs -- <版> <ファイル>...
// 秘密鍵は環境変数 RELEASE_SIGNING_KEY（Base64・32 バイト）から読み、<ファイル>.sig を書き出す。
using PwVault.Core.Updates;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: sign-release.cs <version> <file>...");
    return 2;
}

var keyBase64 = Environment.GetEnvironmentVariable("RELEASE_SIGNING_KEY");
if (string.IsNullOrWhiteSpace(keyBase64))
{
    Console.Error.WriteLine("RELEASE_SIGNING_KEY が設定されていません。");
    return 1;
}

var privateKey = Convert.FromBase64String(keyBase64.Trim());
var version = args[0].TrimStart('v');
foreach (var path in args[1..])
{
    var name = Path.GetFileName(path);
    string signature;
    using (var stream = File.OpenRead(path))
        signature = ReleaseSignature.Sign(privateKey, version, name, stream);

    // 公開鍵で検証できることを確かめてから書き出す（鍵の取り違えを防ぐ）
    using (var stream = File.OpenRead(path))
        if (!ReleaseSignature.Verify(version, name, stream, signature))
        {
            Console.Error.WriteLine("署名を公開鍵で検証できません。アプリに埋め込んだ公開鍵と秘密鍵が対応していません。");
            return 1;
        }

    File.WriteAllText(path + ".sig", signature + "\n");
    Console.WriteLine($"signed {name}");
}
System.Security.Cryptography.CryptographicOperations.ZeroMemory(privateKey);
return 0;

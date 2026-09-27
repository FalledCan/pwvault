using System.Text;
using System.Text.Json.Nodes;
using PwVault.App.Services;
using PwVault.Core.Updates;

namespace PwVault.App.Tests;

public class UpdateTests
{
    private static readonly (byte[] Private, byte[] Public) Keys = ReleaseSignature.GenerateKeyPair();
    private static string Pub => Convert.ToBase64String(Keys.Public);
    private static readonly byte[] NewExe = Encoding.UTF8.GetBytes("new exe contents");

    /// <summary>GitHub の代わり。最新リリースの JSON と、添付ファイルを返す。</summary>
    private static FakeIconServer GitHub(string tag, string? signature = null, byte[]? exe = null, bool withAsset = true, bool prerelease = false)
    {
        var version = tag.TrimStart('v');
        var name = $"PwVault-{version}-win-x64.exe";
        exe ??= NewExe;
        signature ??= ReleaseSignature.Sign(Keys.Private, version, name, new MemoryStream(exe));

        var assets = new JsonArray();
        if (withAsset)
        {
            assets.Add(new JsonObject { ["name"] = name, ["size"] = exe.Length, ["browser_download_url"] = $"https://dl.example/{name}" });
            assets.Add(new JsonObject { ["name"] = name + ".sig", ["size"] = 90, ["browser_download_url"] = $"https://dl.example/{name}.sig" });
        }
        var release = new JsonObject
        {
            ["tag_name"] = tag, ["html_url"] = $"https://github.com/FalledCan/pwvault/releases/tag/{tag}",
            ["body"] = "notes", ["draft"] = false, ["prerelease"] = prerelease, ["assets"] = assets,
        };

        var server = new FakeIconServer();
        server.Pages[$"https://api.github.com/repos/{UpdateService.Repository}/releases/latest"] = ("application/json", Encoding.UTF8.GetBytes(release.ToJsonString()));
        server.Pages[$"https://dl.example/{name}"] = ("application/octet-stream", exe);
        server.Pages[$"https://dl.example/{name}.sig"] = ("text/plain", Encoding.UTF8.GetBytes(signature));
        return server;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Check_FindsNewerRelease_AndIgnoresOlderOrPrerelease()
    {
        var info = await new UpdateService(GitHub("v0.9.0"), Pub).CheckAsync("0.3.0", Ct);
        Assert.NotNull(info);
        Assert.Equal("0.9.0", info.Version);
        Assert.Equal(OperatingSystem.IsWindows(), info.CanInstall);

        Assert.Null(await new UpdateService(GitHub("v0.3.0"), Pub).CheckAsync("0.3.0", Ct));
        Assert.Null(await new UpdateService(GitHub("v0.2.0"), Pub).CheckAsync("0.3.0", Ct));
        Assert.Null(await new UpdateService(GitHub("v0.9.0", prerelease: true), Pub).CheckAsync("0.3.0", Ct));

        // 自動で入れ替えるファイルが無いリリースは「ページを開く」だけになる
        Assert.False((await new UpdateService(GitHub("v0.9.0", withAsset: false), Pub).CheckAsync("0.3.0", Ct))!.CanInstall);
    }

    [Fact]
    public async Task Download_VerifiesSignature()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "自動入れ替えは Windows のみ");
        using var dir = new TempDir();
        var service = new UpdateService(GitHub("v0.9.0"), Pub);
        var info = (await service.CheckAsync("0.3.0", Ct))!;
        var path = await service.DownloadAndVerifyAsync(info, dir.Path, ct: Ct);
        Assert.Equal(NewExe, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("rollback")]
    [InlineData("otherkey")]
    public async Task Download_RejectsBadSignature_AndDeletesFile(string attack)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "自動入れ替えは Windows のみ");
        using var dir = new TempDir();
        var server = attack switch
        {
            // 中身だけ差し替え（署名は本物の中身に対するもの）
            "tampered" => GitHub("v0.9.0", signature: ReleaseSignature.Sign(Keys.Private, "0.9.0", "PwVault-0.9.0-win-x64.exe", new MemoryStream(NewExe)),
                exe: Encoding.UTF8.GetBytes("malicious")),
            // 古い版（0.1.0）に正しく署名されたファイルを 0.9.0 として配る
            "rollback" => GitHub("v0.9.0", signature: ReleaseSignature.Sign(Keys.Private, "0.1.0", "PwVault-0.9.0-win-x64.exe", new MemoryStream(NewExe))),
            // 別の鍵で署名
            _ => GitHub("v0.9.0", signature: ReleaseSignature.Sign(ReleaseSignature.GenerateKeyPair().PrivateKey, "0.9.0", "PwVault-0.9.0-win-x64.exe", new MemoryStream(NewExe))),
        };
        var service = new UpdateService(server, Pub);
        var info = (await service.CheckAsync("0.3.0", Ct))!;

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.DownloadAndVerifyAsync(info, dir.Path, ct: Ct));
        Assert.Contains("署名", ex.Message);
        Assert.Empty(Directory.GetFiles(dir.Path)); // 検証できなかったファイルは残さない
    }

    [Fact]
    public void ReplaceExecutable_SwapsFiles_AndCleansUpOld()
    {
        using var dir = new TempDir();
        var exe = dir.File("PwVault.exe");
        var downloaded = dir.File(".PwVault-0.9.0-win-x64.exe.download");
        File.WriteAllText(exe, "old");
        File.WriteAllText(downloaded, "new");

        UpdateService.ReplaceExecutable(downloaded, exe);
        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(downloaded));

        UpdateService.CleanupAfterUpdate(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void ReplaceExecutable_RollsBack_WhenNewFileMissing()
    {
        using var dir = new TempDir();
        var exe = dir.File("PwVault.exe");
        File.WriteAllText(exe, "old");
        Assert.ThrowsAny<IOException>(() => UpdateService.ReplaceExecutable(dir.File("missing"), exe));
        Assert.Equal("old", File.ReadAllText(exe));
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pwvault-app-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
}

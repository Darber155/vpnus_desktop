using System.Net.Http;
using VpnUs.Core.Update;
using Xunit;
using Xunit.Abstractions;

namespace VpnUs.Core.Tests;

/// <summary>
/// Интеграционная проверка автообновления: реальный релиз из GitHub
/// (если сети нет — тест тихо пропускается).
/// </summary>
public class AppUpdateServiceTests
{
    private readonly ITestOutputHelper _output;

    public AppUpdateServiceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Check_RealRelease_ParsesAndSelectsAssets()
    {
        using var service = new AppUpdateService("VpnUs.Tests/1.0");

        AppRelease? release;
        try
        {
            release = await service.CheckAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _output.WriteLine("Сеть недоступна, тест пропущен: " + ex.Message);
            return;
        }

        if (release is null)
        {
            _output.WriteLine("Релизов нет или GitHub недоступен: " + service.LastCheckError);
            return;
        }

        _output.WriteLine($"release={release.Tag} version={release.Version} assets={release.Assets.Count} newer={release.IsNewer}");
        Assert.NotEmpty(release.Version);
        Assert.NotEmpty(release.Assets);
        Assert.Contains(release.Assets, a => a.Name.Contains("Setup"));
        Assert.Contains(release.Assets, a => a.Name.Contains("portable"));

        var setup = AppUpdateService.SelectAsset(release, portable: false, arch: "x64");
        Assert.NotNull(setup);
        Assert.Contains("x64", setup!.Name);
        Assert.Contains("Setup", setup.Name);

        var portable = AppUpdateService.SelectAsset(release, portable: true, arch: "arm64");
        Assert.NotNull(portable);
        Assert.Contains("arm64", portable!.Name);
        Assert.Contains("portable", portable.Name);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    [InlineData("1.0.0", "1.1.0", -1)]
    [InlineData("v2.0.0-beta", "2.0.0", 0)]
    public void CompareVersions_Works(string left, string right, int expected)
    {
        Assert.Equal(expected, AppUpdateService.CompareVersions(AppUpdateService.Normalize(left), AppUpdateService.Normalize(right)));
    }
}

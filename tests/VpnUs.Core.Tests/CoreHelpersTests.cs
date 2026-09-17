using System.Text;
using VpnUs.Core.Apps;
using VpnUs.Core.Storage;
using VpnUs.Core.Text;
using Xunit;

namespace VpnUs.Core.Tests;

public class Base64UrlTests
{
    [Theory]
    [InlineData("aGVsbG8=", "hello")]
    [InlineData("aGVsbG8", "hello")]
    [InlineData("aGVsbG8h", "hello!")]
    public void TryDecode_HandlesPadding(string input, string expected)
    {
        Assert.True(Base64Url.TryDecode(input, out var text));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void TryDecode_HandlesUrlSafeAlphabet()
    {
        var raw = Encoding.UTF8.GetBytes("a+b/c?d~e");
        var standard = Convert.ToBase64String(raw);
        var urlSafe = standard.Replace('+', '-').Replace('/', '_').TrimEnd('=');

        Assert.True(Base64Url.TryDecode(urlSafe, out var text));
        Assert.Equal("a+b/c?d~e", text);
    }

    [Fact]
    public void TryDecode_RejectsGarbage()
    {
        Assert.False(Base64Url.TryDecode("!!!not base64!!!", out _));
        Assert.False(Base64Url.TryDecode("", out _));
    }
}

public class JsonStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vpnus-test-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new VpnUs.Core.Models.AppSettings
            {
                SubscriptionUrl = "https://example.com/sub",
                Mode = VpnUs.Core.Models.RoutingMode.PerApp,
                AppSplit = VpnUs.Core.Models.AppSplitMode.AllExceptSelected,
                SubscriptionUpdateHours = 6,
                SelectedApps = [new VpnUs.Core.Models.SelectedApp(@"C:\a\b.exe", "B")],
            };

            JsonStore.Save(path, settings);
            var loaded = JsonStore.Load(path, () => new VpnUs.Core.Models.AppSettings());

            Assert.Equal("https://example.com/sub", loaded.SubscriptionUrl);
            Assert.Equal(VpnUs.Core.Models.RoutingMode.PerApp, loaded.Mode);
            Assert.Equal(VpnUs.Core.Models.AppSplitMode.AllExceptSelected, loaded.AppSplit);
            Assert.Equal(6, loaded.SubscriptionUpdateHours);
            Assert.Single(loaded.SelectedApps);

            var json = File.ReadAllText(path);
            Assert.Contains("\"perApp\"", json);
            Assert.Contains("\"allExceptSelected\"", json);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ReturnsFactoryResult_OnBrokenFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vpnus-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not json");
        try
        {
            var loaded = JsonStore.Load(path, () => new VpnUs.Core.Models.AppSettings { LogLevel = "warn" });
            Assert.Equal("warn", loaded.LogLevel);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class FlagHelperTests
{
    [Theory]
    [InlineData("Amsterdam NL", "\U0001F1F3\U0001F1F1")]
    [InlineData("Germany-1", "\U0001F1E9\U0001F1EA")]
    [InlineData("DE-Frankfurt", "\U0001F1E9\U0001F1EA")]
    [InlineData("Tokyo Premium", "\U0001F1EF\U0001F1F5")]
    public void Guess_DetectsCountryFromName(string name, string expectedFlag)
    {
        Assert.Equal(expectedFlag, FlagHelper.Guess(name));
    }

    [Fact]
    public void Guess_ReturnsEmpty_ForUnknownName()
    {
        Assert.Equal("", FlagHelper.Guess("SuperFast-01"));
    }

    [Fact]
    public void Guess_ReturnsEmpty_WhenNameAlreadyHasFlag()
    {
        Assert.Equal("", FlagHelper.Guess("\U0001F1E9\U0001F1EA Berlin"));
    }
}

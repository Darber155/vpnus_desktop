using VpnUs.Core.Text;
using Xunit;

namespace VpnUs.Core.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 15, 30, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Null_shows_placeholder()
    {
        Assert.Equal("Подписка ещё не обновлялась", RelativeTime.FormatSubscriptionUpdate(null, Now));
    }

    [Fact]
    public void Seconds_ago_shows_just_now()
    {
        Assert.Equal(
            "Обновлено: 17.09.2026 15:29 · только что",
            RelativeTime.FormatSubscriptionUpdate(Now.AddSeconds(-30), Now));
    }

    [Fact]
    public void Minutes_ago()
    {
        Assert.Equal(
            "Обновлено: 17.09.2026 15:20 · 10 мин назад",
            RelativeTime.FormatSubscriptionUpdate(Now.AddMinutes(-10), Now));
    }

    [Fact]
    public void Hours_ago()
    {
        Assert.Equal(
            "Обновлено: 17.09.2026 13:30 · 2 ч назад",
            RelativeTime.FormatSubscriptionUpdate(Now.AddHours(-2), Now));
    }

    [Fact]
    public void Days_ago()
    {
        Assert.Equal(
            "Обновлено: 16.09.2026 09:30 · 1 дн назад",
            RelativeTime.FormatSubscriptionUpdate(Now.AddHours(-30), Now));
    }
}

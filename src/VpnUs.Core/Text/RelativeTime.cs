namespace VpnUs.Core.Text;

public static class RelativeTime
{
    /// <summary>«Обновлено: 17.09.2026 14:00 · 5 мин назад».</summary>
    public static string FormatSubscriptionUpdate(DateTimeOffset? updatedAt, DateTimeOffset? now = null)
    {
        if (updatedAt is null)
        {
            return "Подписка ещё не обновлялась";
        }

        var delta = (now ?? DateTimeOffset.Now) - updatedAt.Value;
        var relative = delta.TotalSeconds < 60
            ? "только что"
            : delta.TotalMinutes < 60
                ? $"{delta.TotalMinutes:0} мин назад"
                : delta.TotalHours < 24
                    ? $"{delta.TotalHours:0} ч назад"
                    : $"{delta.TotalDays:0} дн назад";

        return $"Обновлено: {updatedAt.Value:dd.MM.yyyy HH:mm} · {relative}";
    }
}

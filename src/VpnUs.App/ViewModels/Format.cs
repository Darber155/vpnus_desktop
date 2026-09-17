namespace VpnUs.App.ViewModels;

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        < 0 => "0 Б",
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} МБ",
        _ => $"{bytes / 1073741824.0:0.##} ГБ",
    };

    public static string Speed(long bytesPerSecond) => Bytes(bytesPerSecond) + "/с";

    public static string Uptime(TimeSpan time)
        => time.TotalDays >= 1
            ? $"{(int)time.TotalDays} д {time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}";
}

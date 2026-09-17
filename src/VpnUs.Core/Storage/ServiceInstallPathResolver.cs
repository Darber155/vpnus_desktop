namespace VpnUs.Core.Storage;

/// <summary>
/// Куда регистрировать Windows-службу. Раньше путь всегда указывал на
/// Program Files\VpnUs\VpnUs.Service.exe, из-за чего можно было зарегистрировать
/// несуществующий/устаревший бинарник (служба падала с ошибкой 1053).
/// </summary>
public static class ServiceInstallPathResolver
{
    public static (bool Deploy, string ExePath) Resolve(
        string processPath,
        string targetDirectory,
        string programFilesDirectory,
        bool isPortable)
    {
        var sourceDir = Path.GetDirectoryName(processPath) ?? "";

        // Портативный режим и запуск из Program Files — регистрируем ровно тот exe, который запущен.
        if (isPortable || IsUnder(sourceDir, programFilesDirectory))
        {
            return (false, processPath);
        }

        if (PathsEqual(sourceDir, targetDirectory))
        {
            return (false, processPath);
        }

        return (true, Path.Combine(targetDirectory, "VpnUs.Service.exe"));
    }

    private static bool IsUnder(string path, string root)
        => root.Length > 0 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool PathsEqual(string left, string right)
        => string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

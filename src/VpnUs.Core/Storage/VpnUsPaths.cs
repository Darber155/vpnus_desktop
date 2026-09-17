namespace VpnUs.Core.Storage;

/// <summary>
/// Пути приложения. Обычный режим: служба держит данные в ProgramData\VpnUs.
/// Портативный режим: если рядом с exe (или на уровень выше) лежит файл <c>portable.flag</c>,
/// все данные пишутся в <c>&lt;папка&gt;\data</c> и ничего не создаётся в ProgramData/AppData.
/// </summary>
public static class VpnUsPaths
{
    public const string PortableFlagName = "portable.flag";

    private static readonly string? PortableRoot = DetectPortableRoot();

    public static bool IsPortable => PortableRoot is not null;

    public static string PortableRootDirectory => PortableRoot ?? "";

    public static string ProgramDataRoot => PortableRoot is not null
        ? Path.Combine(PortableRoot, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnUs");

    public static string SettingsFile => Path.Combine(ProgramDataRoot, "settings.json");
    public static string NodesFile => Path.Combine(ProgramDataRoot, "nodes.json");
    public static string ConfigFile => Path.Combine(ProgramDataRoot, "config", "config.json");
    public static string TokenFile => Path.Combine(ProgramDataRoot, "ipc.token");
    public static string AllowFile => Path.Combine(ProgramDataRoot, "ipc.allow");
    public static string CoreDir => Path.Combine(ProgramDataRoot, "core");
    public static string CoreExe => Path.Combine(CoreDir, "sing-box.exe");
    public static string WintunDll => Path.Combine(CoreDir, "wintun.dll");
    public static string DataDir => Path.Combine(ProgramDataRoot, "data");
    public static string LogsDir => Path.Combine(ProgramDataRoot, "logs");
    public static string RuleSetsDir => Path.Combine(ProgramDataRoot, "rulesets");
    public static string ServiceLogFile => Path.Combine(LogsDir, "service.log");
    public static string CoreVersionFile => Path.Combine(CoreDir, "version.txt");

    public static string UserRoot => PortableRoot is not null
        ? Path.Combine(PortableRoot, "data", "ui")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VpnUs");

    public static string UiSettingsFile => Path.Combine(UserRoot, "ui.json");
    public static string UiLogFile => Path.Combine(UserRoot, "ui.log");

    public static string PipeName => "vpnus-service";

    /// <summary>Возвращает папку установки приложения (где лежит VpnUs.exe / VpnUs.Service.exe).</summary>
    public static string AppDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    public static void EnsureProgramData()
    {
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        Directory.CreateDirectory(CoreDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(RuleSetsDir);
    }

    /// <summary>Создание папок без исключений: у обычного пользователя нет прав на подпапки,
    /// созданные службой (SYSTEM), — это нормально, писать туда должна только служба.</summary>
    public static bool TryEnsureProgramData(out string? error)
    {
        try
        {
            EnsureProgramData();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            error = $"Нет доступа к {ProgramDataRoot} ({ex.Message}). " +
                    "Установите службу от администратора: VpnUs.Service.exe install — она работает от LocalSystem и создаст папку сама.";
            return false;
        }
    }

    public static void EnsureUserData() => Directory.CreateDirectory(UserRoot);

    /// <summary>Куда регистрировать службу и нужно ли копировать её файлы.</summary>
    public static (bool Deploy, string ExePath) ResolveServiceInstallPath(
        string processPath,
        string targetDirectory,
        string programFilesDirectory)
        => ServiceInstallPathResolver.Resolve(processPath, targetDirectory, programFilesDirectory, IsPortable);

    private static string? DetectPortableRoot()
    {
        try
        {
            var directory = new DirectoryInfo(AppDirectory);
            for (var level = 0; level < 3 && directory is not null; level++, directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, PortableFlagName)))
                {
                    return directory.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return null;
    }
}

public sealed class UiPreferences
{
    public bool StartMinimized { get; set; } = true;
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 720;
    public bool CheckUpdatesOnStart { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
}

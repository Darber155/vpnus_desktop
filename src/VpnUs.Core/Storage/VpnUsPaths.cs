namespace VpnUs.Core.Storage;

public static class VpnUsPaths
{
    public static string ProgramDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VpnUs");

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
    public static string ServiceLogFile => Path.Combine(LogsDir, "service.log");
    public static string CoreVersionFile => Path.Combine(CoreDir, "version.txt");

    public static string UserRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VpnUs");

    public static string UiSettingsFile => Path.Combine(UserRoot, "ui.json");
    public static string UiLogFile => Path.Combine(UserRoot, "ui.log");

    public static string PipeName => "vpnus-service";

    public static void EnsureProgramData()
    {
        Directory.CreateDirectory(ProgramDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFile)!);
        Directory.CreateDirectory(CoreDir);
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
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
}

public sealed class UiPreferences
{
    public bool StartMinimized { get; set; } = true;
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 720;
}

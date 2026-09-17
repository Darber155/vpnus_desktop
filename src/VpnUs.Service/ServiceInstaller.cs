using System.Diagnostics;
using System.Security.Principal;
using VpnUs.Core.Storage;

namespace VpnUs.Service;

/// <summary>Установка/удаление Windows-службы через sc.exe (требует прав администратора).</summary>
public static class ServiceInstaller
{
    public const string ServiceName = "VpnUs";
    public const string DisplayName = "VpnUs VPN (sing-box)";
    public const string Description = "VPN-клиент на ядре sing-box: TUN, per-app split tunneling, подписки Happ.";

    private static readonly System.Text.StringBuilder InstallLogText = new();

    private static void Log(string text)
    {
        Console.WriteLine(text);
        InstallLogText.AppendLine(text);
    }

    private static void LogError(string text)
    {
        Console.Error.WriteLine(text);
        InstallLogText.AppendLine("ERROR: " + text);
    }

    private static void FlushInstallLog()
    {
        try
        {
            VpnUsPaths.TryEnsureProgramData(out _);
            Directory.CreateDirectory(VpnUsPaths.LogsDir);
            File.AppendAllText(
                Path.Combine(VpnUsPaths.LogsDir, "install.log"),
                $"{Environment.NewLine}=== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}{InstallLogText}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static int Install()
    {
        try
        {
            return InstallCore();
        }
        finally
        {
            FlushInstallLog();
        }
    }

    private static int InstallCore()
    {
        var (servicePath, deployError) = DeployFiles();
        if (servicePath is null)
        {
            LogError(deployError!);
            return 1;
        }

        var exe = servicePath;
        Log($"Файлы службы: {exe}");

        if (!File.Exists(exe))
        {
            LogError($"Не найден {exe} — переустановите приложение.");
            return 1;
        }

        var results = new List<(string Args, int Code, string Output)>();
        var create = RunSc("create", ServiceName, "binPath=", $"\"{exe}\" run", "start=", "auto", "DisplayName=", DisplayName);
        results.Add(create);

        // 1073 = ERROR_SERVICE_EXISTS, 1072 = ERROR_SERVICE_MARKED_FOR_DELETE — это «починка» существующей службы.
        if (create.Code != 0 && create.Code is 1073 or 1072)
        {
            Log("Служба уже существует — обновляю путь к исполняемому файлу (sc config).");
            results.Add(RunSc("config", ServiceName, "binPath=", $"\"{exe}\" run", "start=", "auto", "DisplayName=", DisplayName));
            results.Add(RunSc("stop", ServiceName));
            System.Threading.Thread.Sleep(1500);
        }

        results.Add(RunSc("description", ServiceName, Description));
        results.Add(RunSc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/10000/restart/30000"));
        WriteAllowList();
        results.Add(RunSc("start", ServiceName));

        var failedHard = create.Code != 0 && create.Code is not (1073 or 1072);

        foreach (var (args, code, output) in results)
        {
            var text = string.IsNullOrWhiteSpace(output) ? "" : " :: " + output.Trim();
            Log($"sc {args} -> {code}{text}");
        }

        if (failedHard)
        {
            LogError("Не удалось создать службу. Запустите установку от имени администратора.");
            return 1;
        }

        // Проверяем, что служба действительно стартовала: иначе пользователь ничего не узнает
        // (окно установщика, запущенного через UAC, закрывается мгновенно).
        System.Threading.Thread.Sleep(2500);
        var query = RunSc("query", ServiceName);
        Log($"sc query -> {query.Code} {query.Output.Trim()}");
        FlushInstallLog();

        if (!query.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) &&
            !query.Output.Contains("РАБОТАЕТ", StringComparison.OrdinalIgnoreCase))
        {
            LogError($"Служба установлена, но не запустилась. Смотрите {Path.Combine(VpnUsPaths.LogsDir, "install.log")} и service.log");
            return 1;
        }

        Log($"Служба {ServiceName} установлена и запущена ({exe}).");
        return 0;
    }

    public static string TargetDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "VpnUs");

    /// <summary>
    /// Определяет, откуда запускать службу, и при необходимости копирует файлы в Program Files.
    /// Правила: портативный режим или папка Program Files → регистрируем на месте;
    /// иначе (сборка из bin) → копируем в Program Files\VpnUs, чтобы сборка не блокировала файлы.
    /// </summary>
    private static (string? ServiceExePath, string? Error) DeployFiles()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return (null, "Не удалось определить путь к VpnUs.Service.exe");
        }

        var sourceDir = Path.GetDirectoryName(processPath);
        if (string.IsNullOrEmpty(sourceDir))
        {
            return (null, "Не удалось определить папку с файлами службы");
        }

        var target = TargetDirectory;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var (deploy, exePath) = VpnUsPaths.ResolveServiceInstallPath(processPath, target, programFiles);

        if (!deploy)
        {
            return (exePath, null);
        }

        try
        {
            Directory.CreateDirectory(target);

            foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(sourceDir, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }

            return (Path.Combine(target, "VpnUs.Service.exe"), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, $"Не удалось скопировать файлы в {target}: {ex.Message}. Запустите установку от администратора.");
        }
    }

    public static int Uninstall()
    {
        try
        {
            RunSc("stop", ServiceName);
            var delete = RunSc("delete", ServiceName);
            Log($"sc delete {ServiceName} -> {delete.Code} {delete.Output.Trim()}");
            return delete.Code == 0 ? 0 : 1;
        }
        finally
        {
            FlushInstallLog();
        }
    }

    public static int Start() => Report(RunSc("start", ServiceName));

    public static int Stop() => Report(RunSc("stop", ServiceName));

    private static int Report((string Args, int Code, string Output) result)
    {
        Console.WriteLine($"sc {result.Args} -> {result.Code} {result.Output.Trim()}");
        return result.Code;
    }

    private static void WriteAllowList()
    {
        try
        {
            VpnUsPaths.EnsureProgramData();
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            var lines = new List<string> { "# SID пользователей, которым разрешено управлять службой по IPC" };

            if (!string.IsNullOrEmpty(sid))
            {
                lines.Add(sid);
            }

            lines.Add("S-1-5-18");
            lines.Add("S-1-5-32-544");

            File.WriteAllLines(VpnUsPaths.AllowFile, lines);
            Console.WriteLine($"Список доступа IPC: {VpnUsPaths.AllowFile}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine("Не удалось записать ipc.allow: " + ex.Message);
        }
    }

    private static (string Args, int Code, string Output) RunSc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (string.Join(' ', args), -1, "не удалось запустить sc.exe");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return (string.Join(' ', args), process.ExitCode, output.ReplaceLineEndings(" "));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (string.Join(' ', args), -1, ex.Message);
        }
    }
}

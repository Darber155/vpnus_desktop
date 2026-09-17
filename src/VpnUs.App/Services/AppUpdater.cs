using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using VpnUs.Core.Storage;
using VpnUs.Core.Update;

namespace VpnUs.App.Services;

/// <summary>
/// Установка обновления приложения.
/// Установленная версия: скачивается Setup-EXE и запускается через UAC (Inno Setup в тихом режиме).
/// Портативная версия: ZIP распаковывается и заменяет файлы после выхода приложения.
/// </summary>
public sealed class AppUpdater
{
    private readonly AppUpdateService _service = new();

    public static string ArchName => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    public string? LastCheckError => _service.LastCheckError;

    public Task<AppRelease?> CheckAsync(CancellationToken ct = default) => _service.CheckAsync(ct);

    public async Task<(bool Ok, string Message)> DownloadAndApplyAsync(
        AppRelease release,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var asset = AppUpdateService.SelectAsset(release, VpnUsPaths.IsPortable, ArchName);
        if (asset is null || asset.Url.Length == 0)
        {
            var kind = VpnUsPaths.IsPortable ? "portable" : "setup";
            return (false, $"В релизе {release.Tag} нет файла для {ArchName} ({kind}).");
        }

        var updateDir = Path.Combine(Path.GetTempPath(), "vpnus-update");
        Directory.CreateDirectory(updateDir);
        var target = Path.Combine(updateDir, asset.Name);

        try
        {
            await _service.DownloadAsync(asset.Url, target, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            return (false, "Не удалось скачать обновление: " + ex.Message);
        }

        if (VpnUsPaths.IsPortable)
        {
            return ApplyPortableUpdate(target);
        }

        return ApplyInstalledUpdate(target);
    }

    private static (bool Ok, string Message) ApplyInstalledUpdate(string setupPath)
    {
        try
        {
            var psi = new ProcessStartInfo(setupPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            };

            Process.Start(psi);
            return (true, "Установщик запущен (запрос UAC). Приложение закроется и откроется заново после обновления.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, "Не удалось запустить установщик: " + ex.Message + ". Файл обновления: " + setupPath);
        }
    }

    private static (bool Ok, string Message) ApplyPortableUpdate(string zipPath)
    {
        var root = VpnUsPaths.PortableRootDirectory;
        if (root.Length == 0)
        {
            return (false, "Не удалось определить папку портативной версии.");
        }

        var updateDir = Path.Combine(Path.GetTempPath(), "vpnus-update");
        var extractDir = Path.Combine(updateDir, "new");

        try
        {
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return (false, "Не удалось распаковать обновление: " + ex.Message);
        }

        var appExe = Path.Combine(root, "VpnUs.exe");
        var scriptPath = Path.Combine(updateDir, "apply-update.ps1");

        // Не интерполированная raw-строка: {0}..{3} подставляем через string.Format.
        const string scriptTemplate = """
$pidToWait = {0}
try { Wait-Process -Id $pidToWait -Timeout 90 -ErrorAction SilentlyContinue } catch {}
Start-Sleep -Seconds 1
Copy-Item -Path '{1}\*' -Destination '{2}' -Recurse -Force -ErrorAction SilentlyContinue
Start-Process -FilePath '{3}'
""";

        var script = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            scriptTemplate,
            Environment.ProcessId,
            extractDir,
            root,
            appExe);

        try
        {
            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            };

            Process.Start(psi);
            return (true, "Обновление распаковано. Приложение закроется и обновится автоматически.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            return (false, "Не удалось запустить обновление: " + ex.Message + ". Файлы: " + extractDir);
        }
    }
}

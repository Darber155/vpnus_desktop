using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using VpnUs.Core.Models;

namespace VpnUs.Core.Apps;

/// <summary>
/// Сканер установленных приложений: ярлыки меню «Пуск», ключи Uninstall в реестре,
/// пакеты Store (Appx) и запущенные процессы.
/// </summary>
public sealed class InstalledAppScanner
{
    private static readonly string[] SourcePriority = ["Пуск", "Реестр", "Store", "Запущено"];

    private static readonly string[] StartMenuDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
    ];

    private static readonly (RegistryHive Hive, RegistryView View)[] UninstallRoots =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    private static readonly string[] UninstallSubKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    public async Task<List<AppEntry>> ScanAsync(
        bool includeStoreApps = true,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        progress?.Report("Поиск ярлыков в меню «Пуск»...");
        foreach (var item in await Task.Run(ScanStartMenu, ct).ConfigureAwait(false))
        {
            Merge(map, item.Name, item.Path, "Пуск", null);
        }

        ct.ThrowIfCancellationRequested();

        progress?.Report("Чтение списка установленных программ...");
        foreach (var item in await Task.Run(ScanRegistry, ct).ConfigureAwait(false))
        {
            Merge(map, item.Name, item.Path, "Реестр", item.Publisher);
        }

        if (includeStoreApps && !ct.IsCancellationRequested)
        {
            progress?.Report("Перечисление приложений Store...");
            foreach (var item in await ScanStoreAppsAsync(ct).ConfigureAwait(false))
            {
                Merge(map, item.Name, item.Path, "Store", null);
            }
        }

        ct.ThrowIfCancellationRequested();

        progress?.Report("Проверка запущенных процессов...");
        foreach (var item in ScanRunningProcesses())
        {
            if (map.TryGetValue(item.Path, out var entry))
            {
                entry.IsRunning = true;
            }
            else
            {
                Merge(map, item.Name, item.Path, "Запущено", null, isRunning: true);
            }
        }

        return map.Values
            .Where(e => e.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static List<AppEntry> GetRunningProcessEntries()
        => ScanRunningProcesses()
            .Select(p => new AppEntry { Name = p.Name, Path = p.Path, Source = "Запущено", IsRunning = true })
            .ToList();

    private static void Merge(
        Dictionary<string, AppEntry> map,
        string name,
        string path,
        string source,
        string? publisher,
        bool isRunning = false)
    {
        var normalized = path.Trim().Trim('"');
        if (normalized.Length == 0 || !normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            normalized = Path.GetFullPath(normalized);
        }
        catch (ArgumentException)
        {
            return;
        }

        if (!File.Exists(normalized))
        {
            return;
        }

        if (!map.TryGetValue(normalized, out var entry))
        {
            map[normalized] = new AppEntry
            {
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(normalized) : name.Trim(),
                Path = normalized,
                Source = source,
                Publisher = publisher,
                IsRunning = isRunning,
            };
            return;
        }

        entry.IsRunning |= isRunning;
        if (Priority(source) < Priority(entry.Source))
        {
            entry.Source = source;
            if (!string.IsNullOrWhiteSpace(name))
            {
                entry.Name = name.Trim();
            }

            if (!string.IsNullOrWhiteSpace(publisher))
            {
                entry.Publisher ??= publisher;
            }
        }
    }

    private static int Priority(string source)
    {
        var index = Array.IndexOf(SourcePriority, source);
        return index < 0 ? SourcePriority.Length : index;
    }

    private static IEnumerable<(string Name, string Path)> ScanStartMenu()
    {
        var results = new List<(string Name, string Path)>();
        dynamic? shell = null;

        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is not null)
            {
                shell = Activator.CreateInstance(type);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
        }

        foreach (var dir in StartMenuDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var link in links)
            {
                string? target = null;
                try
                {
                    if (shell is not null)
                    {
                        target = (string?)shell.CreateShortcut(link).TargetPath;
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
                {
                }

                if (string.IsNullOrWhiteSpace(target) ||
                    !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add((Path.GetFileNameWithoutExtension(link), target));
            }
        }

        if (shell is not null)
        {
            try
            {
                Marshal.FinalReleaseComObject((object)shell);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidCastException)
            {
            }
        }

        return results;
    }

    private static IEnumerable<(string Name, string Path, string? Publisher)> ScanRegistry()
    {
        var results = new List<(string Name, string Path, string? Publisher)>();

        foreach (var (hive, view) in UninstallRoots)
        {
            foreach (var subKey in UninstallSubKeys)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(subKey);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(name);
                        if (app is null)
                        {
                            continue;
                        }

                        var displayName = app.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            continue;
                        }

                        var exe = ParseIconPath(app.GetValue("DisplayIcon") as string);
                        if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        results.Add((displayName.Trim(), exe, app.GetValue("Publisher") as string));
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                }
            }
        }

        return results;
    }

    private static string? ParseIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim();

        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1)
            {
                return value[1..end];
            }
        }

        var comma = value.LastIndexOf(',');
        if (comma > 2 && int.TryParse(value[(comma + 1)..].Trim(), out _))
        {
            value = value[..comma];
        }

        value = value.Trim().Trim('"');
        return value.Length > 0 ? value : null;
    }

    private static async Task<List<(string Name, string Path)>> ScanStoreAppsAsync(CancellationToken ct)
    {
        var results = new List<(string Name, string Path)>();

        const string script =
            "$ErrorActionPreference='SilentlyContinue';" +
            "Get-AppxPackage | ForEach-Object {" +
            " $p=$_; $exe = Get-ChildItem -LiteralPath $p.InstallLocation -Filter *.exe -File -ErrorAction SilentlyContinue |" +
            " Select-Object -First 1 -ExpandProperty FullName;" +
            " if ($exe) { [pscustomobject]@{N=$p.Name;P=$exe} } } | ConvertTo-Json -Compress";

        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return results;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return results;
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stdout))
            {
                return results;
            }

            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                AddStoreEntry(results, root);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    AddStoreEntry(results, element);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        return results;
    }

    private static void AddStoreEntry(List<(string Name, string Path)> results, JsonElement element)
    {
        var name = element.TryGetProperty("N", out var n) ? n.GetString() : null;
        var path = element.TryGetProperty("P", out var p) ? p.GetString() : null;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
        {
            results.Add((name, path));
        }
    }

    private static List<(string Name, string Path)> ScanRunningProcesses()
    {
        var results = new List<(string Name, string Path)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return results;
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(path) ||
                        path.StartsWith(@"\??\", StringComparison.Ordinal) ||
                        !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    if (seen.Add(path))
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        results.Add((name, path));
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }

        return results;
    }
}

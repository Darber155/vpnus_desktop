using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VpnUs.Core.Ipc;
using VpnUs.Core.Storage;

namespace VpnUs.Service;

/// <summary>Загрузка актуального sing-box.exe и wintun.dll из официальных релизов SagerNet/sing-box.</summary>
public sealed class CoreUpdater
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/SagerNet/sing-box/releases/latest";
    private static readonly string[] RequiredFiles = ["sing-box.exe", "wintun.dll", "libcronet.dll"];

    private readonly RingLog _log;
    private readonly HttpClient _http;

    public CoreUpdater(RingLog log)
    {
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VpnUs/1.0 (Windows; VPN client)");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    public string ArchName => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "386",
        _ => "amd64",
    };

    public CoreInfoDto GetInfo()
    {
        var info = new CoreInfoDto
        {
            Arch = ArchName,
            Path = VpnUsPaths.CoreExe,
            Present = File.Exists(VpnUsPaths.CoreExe),
            WintunPresent = File.Exists(VpnUsPaths.WintunDll),
        };

        try
        {
            if (File.Exists(VpnUsPaths.CoreVersionFile))
            {
                info.Version = File.ReadAllText(VpnUsPaths.CoreVersionFile).Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (info.Version.Length == 0 && info.Present)
        {
            info.Version = "неизвестно";
        }

        info.WintunEmbedded = IsWintunEmbedded(info.Version);
        return info;
    }

    /// <summary>С 1.10.0 wintun.dll встроен в sing-box.exe и в архив не кладётся.</summary>
    public static bool IsWintunEmbedded(string version)
        => Version.TryParse(version.Split('-', '+')[0], out var parsed) && parsed >= new Version(1, 10);

    public async Task<CoreInfoDto> UpdateAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(VpnUsPaths.CoreDir);

        _log.Info("Проверяю обновления ядра sing-box...");

        var json = await _http.GetStringAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
        if (JsonNode.Parse(json) is not JsonObject release)
        {
            throw new InvalidOperationException("GitHub вернул некорректный ответ");
        }

        var tag = release["tag_name"]?.GetValue<string>() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (version.Length == 0)
        {
            throw new InvalidOperationException("Не удалось определить версию релиза");
        }

        var asset = SelectAsset(release);
        if (asset is null)
        {
            throw new InvalidOperationException($"В релизе sing-box {version} нет архива для windows-{ArchName}");
        }

        _log.Info($"Скачиваю {asset.Value.Name} (sing-box {version})...");

        var tempZip = Path.Combine(Path.GetTempPath(), $"vpnus-core-{Guid.NewGuid():N}.zip");
        var tempDir = Path.Combine(Path.GetTempPath(), $"vpnus-core-{Guid.NewGuid():N}");

        try
        {
            using (var response = await _http.GetAsync(asset.Value.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var target = File.Create(tempZip);
                await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(tempDir, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                if (!RequiredFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = Path.Combine(VpnUsPaths.CoreDir, fileName);
                File.Copy(file, target, overwrite: true);
                copied++;
                _log.Info($"Обновлён файл {fileName}");
            }

            if (copied == 0)
            {
                throw new InvalidOperationException("В архиве не найдено sing-box.exe/wintun.dll");
            }

            File.WriteAllText(VpnUsPaths.CoreVersionFile, version);
            _log.Info($"Ядро обновлено до sing-box {version} ({ArchName})");
        }
        finally
        {
            TryDelete(tempZip);
            TryDeleteDirectory(tempDir);
        }

        return GetInfo();
    }

    private (string Name, string Url)? SelectAsset(JsonObject release)
    {
        if (release["assets"] is not JsonArray assets)
        {
            return null;
        }

        var wanted = $"windows-{ArchName}";
        (string Name, string Url)? fallback = null;

        foreach (var item in assets.OfType<JsonObject>())
        {
            var name = item["name"]?.GetValue<string>() ?? "";
            var url = item["browser_download_url"]?.GetValue<string>() ?? "";

            if (url.Length == 0 || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                !name.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                name.Contains("legacy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Contains("windows", StringComparison.OrdinalIgnoreCase))
            {
                return (name, url);
            }

            fallback ??= (name, url);
        }

        return fallback;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

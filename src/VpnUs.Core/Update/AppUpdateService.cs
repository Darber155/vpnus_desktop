using System.Reflection;
using System.Text.Json.Nodes;
using VpnUs.Core.Storage;

namespace VpnUs.Core.Update;

public sealed class AppReleaseAsset
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
}

public sealed class AppRelease
{
    public string Tag { get; set; } = "";
    public string Version { get; set; } = "";
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    public List<AppReleaseAsset> Assets { get; set; } = [];
    public bool IsNewer { get; set; }
}

/// <summary>
/// Проверка и загрузка обновлений приложения с GitHub Releases
/// (репозиторий Darber155/vpnus_desktop, публичный — токен не нужен).
/// </summary>
public sealed class AppUpdateService
{
    public const string Owner = "Darber155";
    public const string Repository = "vpnus_desktop";

    private readonly HttpClient _http;
    private readonly string _userAgent;

    public AppUpdateService(string? userAgent = null)
    {
        _userAgent = string.IsNullOrWhiteSpace(userAgent)
            ? $"VpnUs/{CurrentVersion} (+https://github.com/{Owner}/{Repository})"
            : userAgent;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_userAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    public static string CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.0.0";

    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repository}/releases";

    /// <summary>Последняя ошибка проверки (для понятного текста в UI).</summary>
    public string? LastCheckError { get; private set; }

    public async Task<AppRelease?> CheckAsync(CancellationToken ct = default)
    {
        LastCheckError = null;

        string json;
        try
        {
            json = await _http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repository}/releases/latest", ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            LastCheckError = "На GitHub ещё нет опубликованных релизов";
            return null;
        }
        catch (HttpRequestException ex)
        {
            LastCheckError = $"GitHub недоступен: {ex.Message}";
            return null;
        }
        catch (TaskCanceledException)
        {
            LastCheckError = "Таймаут запроса к GitHub";
            return null;
        }

        if (JsonNode.Parse(json) is not JsonObject root)
        {
            LastCheckError = "GitHub вернул некорректный ответ";
            return null;
        }

        var tag = root["tag_name"]?.GetValue<string>() ?? "";
        var release = new AppRelease
        {
            Tag = tag,
            Version = Normalize(tag),
            Title = root["name"]?.GetValue<string>() ?? tag,
            Notes = root["body"]?.GetValue<string>() ?? "",
            PageUrl = root["html_url"]?.GetValue<string>() ?? ReleasesPageUrl,
        };

        if (DateTimeOffset.TryParse(root["published_at"]?.GetValue<string>(), out var published))
        {
            release.PublishedAt = published;
        }

        if (root["assets"] is JsonArray assets)
        {
            foreach (var item in assets.OfType<JsonObject>())
            {
                release.Assets.Add(new AppReleaseAsset
                {
                    Name = item["name"]?.GetValue<string>() ?? "",
                    Url = item["browser_download_url"]?.GetValue<string>() ?? "",
                    Size = item["size"]?.GetValue<long>() ?? 0,
                });
            }
        }

        release.IsNewer = CompareVersions(release.Version, CurrentVersion) > 0;
        return release;
    }

    public static string Normalize(string tag) => tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];

    public static int CompareVersions(string left, string right)
    {
        static Version Parse(string value)
            => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0, 0);

        return Parse(left).CompareTo(Parse(right));
    }

    /// <summary>Выбирает ассет под архитектуру и режим (Setup для установленной версии, ZIP для портативной).</summary>
    public static AppReleaseAsset? SelectAsset(AppRelease release, bool portable, string arch)
    {
        var kind = portable ? "portable" : "setup";

        return release.Assets.FirstOrDefault(a =>
                   a.Name.Contains(kind, StringComparison.OrdinalIgnoreCase) &&
                   a.Name.Contains(arch, StringComparison.OrdinalIgnoreCase)) ??
               release.Assets.FirstOrDefault(a => a.Name.Contains(kind, StringComparison.OrdinalIgnoreCase));
    }

    public async Task DownloadAsync(string url, string targetPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(targetPath);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            if (total > 0)
            {
                progress?.Report((double)copied / total);
            }
        }
    }
}

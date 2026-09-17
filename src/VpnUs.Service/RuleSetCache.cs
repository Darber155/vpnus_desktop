using System.Net;
using VpnUs.Core.Config;
using VpnUs.Core.Storage;

namespace VpnUs.Service;

/// <summary>
/// Локальный кэш rule-set'ов (.srs). Служба скачивает наборы сама и подсовывает sing-box
/// локальные пути: если GitHub (или прокси) недоступен, туннель всё равно поднимается,
/// просто соответствующие правила не применяются до следующего успешного обновления.
/// </summary>
public sealed class RuleSetCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);
    private readonly RingLog _log;

    public RuleSetCache(RingLog log)
    {
        _log = log;
    }

    public static string PathFor(string tag) => Path.Combine(VpnUsPaths.RuleSetsDir, RuleSetCatalog.FileName(tag));

    public static bool Exists(string tag) => File.Exists(PathFor(tag));

    /// <summary>Сколько наборов отсутствует или устарело.</summary>
    public IReadOnlyList<(string Tag, string Url)> Stale(IEnumerable<(string Tag, string Url)> sets)
    {
        var result = new List<(string Tag, string Url)>();

        foreach (var (tag, url) in sets)
        {
            var path = PathFor(tag);
            if (!File.Exists(path) || DateTimeOffset.Now - File.GetLastWriteTimeUtc(path) > MaxAge)
            {
                result.Add((tag, url));
            }
        }

        return result;
    }

    /// <summary>
    /// Скачивает наборы (best-effort). <paramref name="proxy"/> — адрес локального прокси службы,
    /// чтобы трафик шёл через VPN (GitHub в РФ часто недоступен напрямую).
    /// Возвращает число успешно обновлённых файлов.
    /// </summary>
    public async Task<int> UpdateAsync(
        IEnumerable<(string Tag, string Url)> sets,
        string? proxy,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(VpnUsPaths.RuleSetsDir);

        var updated = 0;

        foreach (var (tag, url) in sets)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var handler = new HttpClientHandler();
                if (!string.IsNullOrWhiteSpace(proxy))
                {
                    handler.Proxy = new WebProxy(proxy);
                    handler.UseProxy = true;
                }
                else
                {
                    handler.UseProxy = false;
                }

                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("VpnUs/1.0 (rule-set updater)");

                var bytes = await client.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    _log.Warn($"rule-set {tag}: пустой ответ");
                    continue;
                }

                var path = PathFor(tag);
                var tmp = path + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);

                if (File.Exists(path))
                {
                    File.Replace(tmp, path, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, path);
                }

                updated++;
                _log.Info($"rule-set {tag}: обновлён ({bytes.Length / 1024} КБ)");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                _log.Warn($"rule-set {tag}: не удалось скачать ({ex.Message}) — используется предыдущая копия");
            }
        }

        return updated;
    }
}

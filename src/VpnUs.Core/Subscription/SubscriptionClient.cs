using System.Net;
using System.Net.Http.Headers;
using VpnUs.Core.Models;

namespace VpnUs.Core.Subscription;

/// <summary>
/// Загрузчик подписки. Повторяет поведение Happ/v2rayNG:
/// User-Agent "v2rayNG/1.8.5", общий cookie jar на все запросы и ручная обработка
/// редиректов (307 без Location = повтор того же URL уже с полученной cookie).
/// </summary>
public sealed class SubscriptionClient : IDisposable
{
    public const string DefaultUserAgent = "v2rayNG/1.8.5";

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private bool _disposed;

    public SubscriptionClient(string? userAgent = null)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(45),
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en;q=0.8");
    }

    public async Task<SubscriptionFetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        var result = new SubscriptionFetchResult();

        if (string.IsNullOrWhiteSpace(url))
        {
            result.Error = "URL подписки не задан";
            return result;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var current) ||
            (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps))
        {
            result.Error = "Некорректный URL подписки";
            return result;
        }

        result.EffectiveUrl = current.ToString();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                result.Error = "Таймаут запроса подписки";
                return result;
            }
            catch (HttpRequestException ex)
            {
                result.Error = "Ошибка сети: " + ex.Message;
                return result;
            }

            using (response)
            {
                result.StatusCode = (int)response.StatusCode;

                if (IsRedirect(response.StatusCode))
                {
                    result.Redirects++;
                    var location = response.Headers.Location;
                    if (location is not null)
                    {
                        // Абсолютный или относительный Location.
                        current = location.IsAbsoluteUri ? location : new Uri(current, location);
                        result.EffectiveUrl = current.ToString();
                    }

                    // Location может отсутствовать: провайдер отдал 307 + Set-Cookie
                    // и ждёт повторный запрос на тот же URL.
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    result.Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                    return result;
                }

                result.Body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                result.Success = true;
                return result;
            }
        }

        result.Error = "Слишком много редиректов (возможно, подписка требует cookie, которую сервер не отдал)";
        return result;
    }

    public async Task<(SubscriptionFetchResult Fetch, SubscriptionParseResult Parse)> FetchAndParseAsync(
        string url,
        CancellationToken ct = default)
    {
        var fetch = await FetchAsync(url, ct).ConfigureAwait(false);
        if (!fetch.Success)
        {
            return (fetch, new SubscriptionParseResult { Error = fetch.Error });
        }

        return (fetch, ShareLinkParser.ParseSubscription(fetch.Body));
    }

    private static bool IsRedirect(HttpStatusCode code) => code is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect or
        HttpStatusCode.MultipleChoices or
        HttpStatusCode.UseProxy;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }
}

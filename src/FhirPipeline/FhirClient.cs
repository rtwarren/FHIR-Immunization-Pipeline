using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;

namespace FhirPipeline;

/// <summary>
/// Pages through a FHIR Immunization search, following Bundle "next" links,
/// with retries for rate limiting (429) and server errors (5xx).
/// </summary>
public sealed class FhirClient
{
    private readonly HttpClient _http;
    private readonly int _maxRetries;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public FhirClient(HttpClient http, int maxRetries = 3, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _maxRetries = maxRetries;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    public static string BuildInitialQuery(Uri baseUrl, int pageSize, DateTimeOffset? since)
    {
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var url = $"{baseUrl.ToString().TrimEnd('/')}/Immunization" +
                  $"?_count={pageSize.ToString(CultureInfo.InvariantCulture)}" +
                  "&_include=Immunization:patient" +
                  "&_sort=_lastUpdated";
        if (since is { } s)
        {
            var stamp = s.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            url += "&_lastUpdated=gt" + stamp;
        }
        return url;
    }

    public async IAsyncEnumerable<BundlePage> GetPagesAsync(
        string firstUrl,
        int maxPages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? url = firstUrl;
        var pages = 0;
        while (url is not null && pages < maxPages)
        {
            var json = await GetWithRetryAsync(url, cancellationToken).ConfigureAwait(false);
            var page = BundleParser.Parse(json);
            yield return page;
            url = page.NextLink;
            pages++;
        }
    }

    private async Task<string> GetWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/fhir+json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            var status = (int)response.StatusCode;
            var transient = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
            if (!transient || attempt >= _maxRetries)
            {
                throw new HttpRequestException(
                    $"FHIR server returned {status} for {url}", null, response.StatusCode);
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;

namespace FHIRPipeline;

/// <summary>
/// Pages through a FHIR Immunization search, following Bundle "next" links,
/// with retries for rate limiting (429) and server errors (5xx).
/// </summary>
public sealed class FHIRClient
{
    private readonly HttpClient _HTTP;
    private readonly int _maxRetries;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public FHIRClient(HttpClient HTTP, int maxRetries = 3, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _HTTP = HTTP;
        _maxRetries = maxRetries;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, ct));
    }

    public static string BuildInitialQuery(Uri baseURL, int pageSize, DateTimeOffset? since)
    {
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var URL = $"{baseURL.ToString().TrimEnd('/')}/Immunization" +
                  $"?_count={pageSize.ToString(CultureInfo.InvariantCulture)}" +
                  "&_include=Immunization:patient" +
                  "&_sort=_lastUpdated";
        if (since is { } s)
        {
            var stamp = s.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            URL += "&_lastUpdated=gt" + stamp;
        }
        return URL;
    }

    public async IAsyncEnumerable<BundlePage> GetPagesAsync(
        string firstURL,
        int maxPages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? URL = firstURL;
        var pages = 0;
        while (URL is not null && pages < maxPages)
        {
            var bundleJSON = await GetWithRetryAsync(URL, cancellationToken).ConfigureAwait(false);
            var page = BundleParser.Parse(bundleJSON);
            yield return page;
            URL = page.NextLink;
            pages++;
        }
    }

    private async Task<string> GetWithRetryAsync(string URL, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, URL);
            request.Headers.Accept.ParseAdd("application/fhir+json");

            using var response = await _HTTP.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            var status = (int)response.StatusCode;
            var transient = response.StatusCode == HttpStatusCode.TooManyRequests || status >= 500;
            if (!transient || attempt >= _maxRetries)
            {
                throw new HttpRequestException(
                    $"FHIR server returned {status} for {URL}", null, response.StatusCode);
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}

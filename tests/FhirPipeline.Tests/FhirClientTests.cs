using System.Net;
using System.Text;

namespace FhirPipeline.Tests;

public class FhirClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
        public List<string> Requests { get; } = new();

        public StubHandler Enqueue(HttpStatusCode status, string body = "", TimeSpan? retryAfter = null)
        {
            _responses.Enqueue(_ =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/fhir+json"),
                };
                if (retryAfter is { } wait) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(wait);
                return response;
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            Assert.Contains("application/fhir+json", request.Headers.Accept.ToString());
            return Task.FromResult(_responses.Dequeue()(request));
        }
    }

    private static (FhirClient Client, StubHandler Handler, List<TimeSpan> Delays) Create(int maxRetries = 3)
    {
        var handler = new StubHandler();
        var delays = new List<TimeSpan>();
        var client = new FhirClient(new HttpClient(handler), maxRetries, (wait, _) =>
        {
            delays.Add(wait);
            return Task.CompletedTask;
        });
        return (client, handler, delays);
    }

    private static async Task<List<BundlePage>> Collect(IAsyncEnumerable<BundlePage> pages)
    {
        var list = new List<BundlePage>();
        await foreach (var p in pages) list.Add(p);
        return list;
    }

    [Fact]
    public void Initial_query_includes_paging_sort_and_include()
    {
        var url = FhirClient.BuildInitialQuery(new Uri("https://fhir.example.org/r4/"), 25, null);
        Assert.Equal(
            "https://fhir.example.org/r4/Immunization?_count=25&_include=Immunization:patient&_sort=_lastUpdated",
            url);
    }

    [Fact]
    public void Incremental_query_uses_utc_watermark()
    {
        var since = new DateTimeOffset(2026, 3, 4, 15, 20, 0, TimeSpan.FromHours(-6));
        var url = FhirClient.BuildInitialQuery(new Uri("https://fhir.example.org/r4"), 10, since);
        Assert.EndsWith("&_lastUpdated=gt2026-03-04T21:20:00Z", url);
    }

    [Fact]
    public async Task Follows_next_links_until_last_page()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, SampleBundles.Page("https://x/page2", ("a", "2026-01-01T00:00:00Z")))
               .Enqueue(HttpStatusCode.OK, SampleBundles.Page("https://x/page3", ("b", "2026-01-02T00:00:00Z")))
               .Enqueue(HttpStatusCode.OK, SampleBundles.Page(null, ("c", "2026-01-03T00:00:00Z")));

        var pages = await Collect(client.GetPagesAsync("https://x/page1", maxPages: 10));

        Assert.Equal(3, pages.Count);
        Assert.Equal(new[] { "https://x/page1", "https://x/page2", "https://x/page3" }, handler.Requests);
    }

    [Fact]
    public async Task Stops_at_max_pages()
    {
        var (client, handler, _) = Create();
        handler.Enqueue(HttpStatusCode.OK, SampleBundles.Page("https://x/page2", ("a", "2026-01-01T00:00:00Z")))
               .Enqueue(HttpStatusCode.OK, SampleBundles.Page("https://x/page3", ("b", "2026-01-02T00:00:00Z")));

        var pages = await Collect(client.GetPagesAsync("https://x/page1", maxPages: 2));

        Assert.Equal(2, pages.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retries_transient_errors_with_backoff_and_retry_after()
    {
        var (client, handler, delays) = Create();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable)
               .Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(7))
               .Enqueue(HttpStatusCode.OK, SampleBundles.Page(null, ("a", "2026-01-01T00:00:00Z")));

        var pages = await Collect(client.GetPagesAsync("https://x/page1", maxPages: 1));

        Assert.Single(pages);
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(7) }, delays);
    }

    [Fact]
    public async Task Gives_up_after_max_retries()
    {
        var (client, handler, delays) = Create(maxRetries: 2);
        handler.Enqueue(HttpStatusCode.BadGateway)
               .Enqueue(HttpStatusCode.BadGateway)
               .Enqueue(HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Collect(client.GetPagesAsync("https://x/p", 1)));
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Does_not_retry_client_errors()
    {
        var (client, handler, delays) = Create();
        handler.Enqueue(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(client.GetPagesAsync("https://x/p", 1)));
        Assert.Empty(delays);
        Assert.Single(handler.Requests);
    }
}

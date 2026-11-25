using System.Net;
using System.Text;

namespace FHIRPipeline.Tests;

public class PipelineRunnerTests
{
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public List<string> Requests { get; } = new();

        public SequenceHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/fhir+json"),
            });
        }
    }

    private static readonly Uri Base = new("https://fhir.example.org/r4");

    [Fact]
    public async Task Full_load_writes_all_pages_and_sets_watermark_to_newest_resource()
    {
        var handler = new SequenceHandler(
            SampleBundles.Page("https://x/2", ("a", "2026-01-03T00:00:00Z"), ("b", "2026-01-01T00:00:00Z")),
            SampleBundles.Page(null, ("c", "2026-01-02T00:00:00Z")));
        var sink = new InMemorySink();
        var runner = new PipelineRunner(new FHIRClient(new HttpClient(handler)), sink, TextWriter.Null);

        var summary = await runner.RunAsync(Base, pageSize: 2, maxPages: 10, fullReload: true);

        Assert.True(sink.Began);
        Assert.Equal(2, summary.Pages);
        Assert.Equal(3, summary.Immunizations);
        Assert.Equal(3, sink.Immunizations.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-01-03T00:00:00Z"), summary.Watermark);
        Assert.Equal(summary, sink.LastSummary);
        Assert.DoesNotContain("_lastUpdated=gt", handler.Requests[0]);
    }

    [Fact]
    public async Task Incremental_load_requests_changes_since_stored_watermark()
    {
        var handler = new SequenceHandler(SampleBundles.Page(null));
        var sink = new InMemorySink { Watermark = DateTimeOffset.Parse("2026-02-01T12:00:00Z") };
        var runner = new PipelineRunner(new FHIRClient(new HttpClient(handler)), sink, TextWriter.Null);

        var summary = await runner.RunAsync(Base, pageSize: 50, maxPages: 5, fullReload: false);

        Assert.Contains("_lastUpdated=gt2026-02-01T12:00:00Z", handler.Requests[0]);
        Assert.Equal(0, summary.Immunizations);
        // Nothing new: the watermark stays where it was rather than resetting.
        Assert.Equal(DateTimeOffset.Parse("2026-02-01T12:00:00Z"), sink.Watermark);
    }

    [Fact]
    public async Task Full_reload_ignores_stored_watermark()
    {
        var handler = new SequenceHandler(SampleBundles.Page(null, ("a", "2026-01-01T00:00:00Z")));
        var sink = new InMemorySink { Watermark = DateTimeOffset.Parse("2026-06-01T00:00:00Z") };
        var runner = new PipelineRunner(new FHIRClient(new HttpClient(handler)), sink, TextWriter.Null);

        await runner.RunAsync(Base, pageSize: 50, maxPages: 5, fullReload: true);

        Assert.DoesNotContain("_lastUpdated", handler.Requests[0]);
    }

    [Fact]
    public void SQL_staging_tables_map_nulls_and_types()
    {
        var batch = Guid.NewGuid();
        var table = SQLServerSink.ToImmunizationTable(new[]
        {
            new ImmunizationRecord("i1", null, null, "p1", "completed", null, "208", null,
                new DateOnly(2026, 1, 2), null, null, null, null),
        }, batch);

        var row = Assert.Single(table.Rows.Cast<System.Data.DataRow>());
        Assert.Equal(batch, row["BatchID"]);
        Assert.Equal(DBNull.Value, row["VersionID"]);
        Assert.Equal(new DateTime(2026, 1, 2), row["OccurrenceDate"]);
        Assert.Equal(DBNull.Value, row["PrimarySource"]);
    }

    [Theory]
    [InlineData(new[] { "--dry-run" }, true)]
    [InlineData(new[] { "--connection", "Server=.;Database=x", "--page-size", "100" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--dry-run", "--page-size", "0" }, false)]
    [InlineData(new[] { "--dry-run", "--unknown" }, false)]
    public void CLI_options_validation(string[] args, bool valid)
    {
        var previous = Environment.GetEnvironmentVariable("FHIR_PIPELINE_CONNECTION");
        Environment.SetEnvironmentVariable("FHIR_PIPELINE_CONNECTION", null);
        try
        {
            Assert.Equal(valid, CLIOptions.Parse(args) is not null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FHIR_PIPELINE_CONNECTION", previous);
        }
    }
}

namespace FhirPipeline;

public sealed class PipelineRunner
{
    private readonly FhirClient _client;
    private readonly IRecordSink _sink;
    private readonly TextWriter _log;

    public PipelineRunner(FhirClient client, IRecordSink sink, TextWriter log)
    {
        _client = client;
        _sink = sink;
        _log = log;
    }

    /// <summary>
    /// Runs one load. Incremental by default: only resources updated after the stored
    /// watermark are requested. The watermark only advances after every page is written.
    /// </summary>
    public async Task<RunSummary> RunAsync(
        Uri baseUrl,
        int pageSize,
        int maxPages,
        bool fullReload,
        CancellationToken cancellationToken = default)
    {
        var previous = fullReload ? null : await _sink.GetWatermarkAsync(cancellationToken).ConfigureAwait(false);
        var url = FhirClient.BuildInitialQuery(baseUrl, pageSize, previous);
        _log.WriteLine(previous is null ? "Full load." : $"Incremental load since {previous.Value:O}.");
        _log.WriteLine($"GET {url}");

        await _sink.BeginAsync(cancellationToken).ConfigureAwait(false);

        int pages = 0, patients = 0, immunizations = 0, warnings = 0;
        var watermark = previous;

        await foreach (var page in _client.GetPagesAsync(url, maxPages, cancellationToken).ConfigureAwait(false))
        {
            await _sink.WriteAsync(page, cancellationToken).ConfigureAwait(false);
            pages++;
            patients += page.Patients.Count;
            immunizations += page.Immunizations.Count;
            warnings += page.Warnings.Count;
            foreach (var w in page.Warnings) _log.WriteLine($"WARN {w}");

            foreach (var i in page.Immunizations)
            {
                if (i.LastUpdated is { } updated && (watermark is null || updated > watermark))
                {
                    watermark = updated;
                }
            }
        }

        var summary = new RunSummary(pages, patients, immunizations, warnings, watermark);
        await _sink.CompleteAsync(summary, cancellationToken).ConfigureAwait(false);
        _log.WriteLine(
            $"Done: {pages} pages, {immunizations} immunizations, {patients} patients, {warnings} warnings. " +
            $"Watermark: {(watermark is null ? "none" : watermark.Value.ToString("O"))}.");
        return summary;
    }
}

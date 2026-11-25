using System.Globalization;
using FhirPipeline;

var options = CliOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(CliOptions.Usage);
    return 2;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("fhir-immunization-pipeline/1.0");
var client = new FhirClient(http);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    if (options.DryRun)
    {
        var sink = new InMemorySink();
        var runner = new PipelineRunner(client, sink, Console.Out);
        await runner.RunAsync(options.BaseUrl, options.PageSize, options.MaxPages, fullReload: true, cts.Token);

        Console.WriteLine();
        Console.WriteLine("Top vaccines in this sample:");
        foreach (var group in sink.Immunizations
                     .GroupBy(i => i.VaccineDisplay ?? i.VaccineCode ?? "(uncoded)")
                     .OrderByDescending(g => g.Count())
                     .Take(10))
        {
            Console.WriteLine($"  {group.Count(),5}  {group.Key}");
        }
    }
    else
    {
        await using var sink = new SqlServerSink(options.ConnectionString!);
        var runner = new PipelineRunner(client, sink, Console.Out);
        await runner.RunAsync(options.BaseUrl, options.PageSize, options.MaxPages, options.FullReload, cts.Token);
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. Core tables and watermark were not changed.");
    return 130;
}
catch (Exception ex) when (ex is HttpRequestException or Microsoft.Data.SqlClient.SqlException
                               or FormatException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

internal sealed record CliOptions(
    Uri BaseUrl,
    string? ConnectionString,
    int PageSize,
    int MaxPages,
    bool FullReload,
    bool DryRun)
{
    public const string DefaultBaseUrl = "https://hapi.fhir.org/baseR4";

    public const string Usage = """
        Usage: FhirPipeline [options]

          --fhir-base <url>     FHIR R4 base URL (default: https://hapi.fhir.org/baseR4)
          --connection <conn>   SQL Server connection string
                                (or set FHIR_PIPELINE_CONNECTION)
          --page-size <n>       Resources per page (default: 50)
          --max-pages <n>       Stop after this many pages (default: 20)
          --full                Ignore the watermark and reload everything
          --dry-run             Fetch and parse only; print a summary, no database
        """;

    public static CliOptions? Parse(string[] args)
    {
        var baseUrl = DefaultBaseUrl;
        var connection = Environment.GetEnvironmentVariable("FHIR_PIPELINE_CONNECTION");
        var pageSize = 50;
        var maxPages = 20;
        var full = false;
        var dryRun = false;

        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--fhir-base":
                    baseUrl = Next() ?? baseUrl;
                    break;
                case "--connection":
                    connection = Next();
                    break;
                case "--page-size":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize < 1) return null;
                    break;
                case "--max-pages":
                    if (!int.TryParse(Next(), NumberStyles.None, CultureInfo.InvariantCulture, out maxPages) || maxPages < 1) return null;
                    break;
                case "--full":
                    full = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    return null;
            }
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        if (!dryRun && string.IsNullOrWhiteSpace(connection)) return null;
        return new CliOptions(uri, connection, pageSize, maxPages, full, dryRun);
    }
}

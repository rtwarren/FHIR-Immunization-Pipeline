using System.Globalization;
using FHIRPipeline;

var options = CLIOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine(CLIOptions.Usage);
    return 2;
}

using var HTTP = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
HTTP.DefaultRequestHeaders.UserAgent.ParseAdd("FHIR-Immunization-Pipeline/1.0");
var client = new FHIRClient(HTTP);

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
        await runner.RunAsync(options.BaseURL, options.PageSize, options.MaxPages, fullReload: true, cts.Token);

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
        await using var sink = new SQLServerSink(options.ConnectionString!);
        var runner = new PipelineRunner(client, sink, Console.Out);
        await runner.RunAsync(options.BaseURL, options.PageSize, options.MaxPages, options.FullReload, cts.Token);
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

internal sealed record CLIOptions(
    Uri BaseURL,
    string? ConnectionString,
    int PageSize,
    int MaxPages,
    bool FullReload,
    bool DryRun)
{
    public const string DefaultBaseURL = "https://hapi.fhir.org/baseR4";

    public const string Usage = """
        Usage: FHIRPipeline [options]

          --fhir-base <URL>     FHIR R4 base URL (default: https://hapi.fhir.org/baseR4)
          --connection <conn>   SQL Server connection string
                                (or set FHIR_PIPELINE_CONNECTION)
          --page-size <n>       Resources per page (default: 50)
          --max-pages <n>       Stop after this many pages (default: 20)
          --full                Ignore the watermark and reload everything
          --dry-run             Fetch and parse only; print a summary, no database
        """;

    public static CLIOptions? Parse(string[] args)
    {
        var baseURL = DefaultBaseURL;
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
                    baseURL = Next() ?? baseURL;
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

        if (!Uri.TryCreate(baseURL, UriKind.Absolute, out var URI)) return null;
        if (!dryRun && string.IsNullOrWhiteSpace(connection)) return null;
        return new CLIOptions(URI, connection, pageSize, maxPages, full, dryRun);
    }
}

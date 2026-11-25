namespace FHIRPipeline;

/// <summary>
/// Destination for parsed records. A run calls BeginAsync once, WriteAsync per page,
/// then CompleteAsync with the new watermark.
/// </summary>
public interface IRecordSink
{
    Task<DateTimeOffset?> GetWatermarkAsync(CancellationToken cancellationToken);
    Task BeginAsync(CancellationToken cancellationToken);
    Task WriteAsync(BundlePage page, CancellationToken cancellationToken);
    Task CompleteAsync(RunSummary summary, CancellationToken cancellationToken);
}

/// <summary>Keeps records in memory. Used for --dry-run and in tests.</summary>
public sealed class InMemorySink : IRecordSink
{
    public DateTimeOffset? Watermark { get; set; }
    public List<PatientRecord> Patients { get; } = new();
    public List<ImmunizationRecord> Immunizations { get; } = new();
    public RunSummary? LastSummary { get; private set; }
    public bool Began { get; private set; }

    public Task<DateTimeOffset?> GetWatermarkAsync(CancellationToken cancellationToken) => Task.FromResult(Watermark);

    public Task BeginAsync(CancellationToken cancellationToken)
    {
        Began = true;
        return Task.CompletedTask;
    }

    public Task WriteAsync(BundlePage page, CancellationToken cancellationToken)
    {
        Patients.AddRange(page.Patients);
        Immunizations.AddRange(page.Immunizations);
        return Task.CompletedTask;
    }

    public Task CompleteAsync(RunSummary summary, CancellationToken cancellationToken)
    {
        LastSummary = summary;
        Watermark = summary.Watermark;
        return Task.CompletedTask;
    }
}

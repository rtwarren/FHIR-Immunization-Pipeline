namespace FHIRPipeline;

/// <summary>
/// The minimum patient attributes needed for immunization coverage reporting.
/// Names, identifiers, full birth dates and street addresses are dropped at parse time.
/// </summary>
public sealed record PatientRecord(
    string ID,
    string? VersionID,
    DateTimeOffset? LastUpdated,
    string? Gender,
    int? BirthYear,
    string? PostalPrefix);

public sealed record ImmunizationRecord(
    string ID,
    string? VersionID,
    DateTimeOffset? LastUpdated,
    string PatientID,
    string Status,
    string? VaccineSystem,
    string? VaccineCode,
    string? VaccineDisplay,
    DateOnly? OccurrenceDate,
    string? LotNumber,
    string? Route,
    string? Site,
    bool? PrimarySource);

public sealed class BundlePage
{
    public List<PatientRecord> Patients { get; } = new();
    public List<ImmunizationRecord> Immunizations { get; } = new();
    public List<string> Warnings { get; } = new();
    public string? NextLink { get; set; }
}

public sealed record RunSummary(
    int Pages,
    int Patients,
    int Immunizations,
    int Warnings,
    DateTimeOffset? Watermark);

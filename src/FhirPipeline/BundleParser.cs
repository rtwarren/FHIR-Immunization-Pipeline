using System.Globalization;
using System.Text.Json;

namespace FhirPipeline;

/// <summary>
/// Parses FHIR R4 searchset or transaction Bundles into flat records.
/// Uses System.Text.Json directly so the mapping from FHIR elements to columns is explicit.
/// </summary>
public static class BundleParser
{
    public const string CvxSystem = "http://hl7.org/fhir/sid/cvx";
    public const string SnomedSystem = "http://snomed.info/sct";

    public static BundlePage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (GetString(root, "resourceType") != "Bundle")
        {
            throw new FormatException("Expected a FHIR Bundle resource.");
        }

        var page = new BundlePage();

        if (root.TryGetProperty("link", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            foreach (var link in links.EnumerateArray())
            {
                if (GetString(link, "relation") == "next")
                {
                    page.NextLink = GetString(link, "url");
                }
            }
        }

        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return page;
        }

        // Transaction bundles (for example from Synthea) reference patients by fullUrl.
        var fullUrlToPatientId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource)) continue;
            if (GetString(resource, "resourceType") != "Patient") continue;
            var fullUrl = GetString(entry, "fullUrl");
            var id = GetString(resource, "id") ?? IdFromFullUrl(fullUrl);
            if (fullUrl is not null && id is not null)
            {
                fullUrlToPatientId[fullUrl] = id;
            }
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("resource", out var resource)) continue;
            var fullUrl = GetString(entry, "fullUrl");
            switch (GetString(resource, "resourceType"))
            {
                case "Patient":
                    var patient = ParsePatient(resource, fullUrl);
                    if (patient is null) page.Warnings.Add("Skipped Patient with no id.");
                    else page.Patients.Add(patient);
                    break;
                case "Immunization":
                    var immunization = ParseImmunization(resource, fullUrl, fullUrlToPatientId, page.Warnings);
                    if (immunization is not null) page.Immunizations.Add(immunization);
                    break;
            }
        }

        return page;
    }

    internal static PatientRecord? ParsePatient(JsonElement resource, string? fullUrl)
    {
        var id = GetString(resource, "id") ?? IdFromFullUrl(fullUrl);
        if (id is null) return null;

        var (versionId, lastUpdated) = ParseMeta(resource);

        int? birthYear = null;
        var birthDate = GetString(resource, "birthDate");
        if (birthDate is { Length: >= 4 } &&
            int.TryParse(birthDate.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            birthYear = year;
        }

        string? postalPrefix = null;
        if (resource.TryGetProperty("address", out var addresses) &&
            addresses.ValueKind == JsonValueKind.Array &&
            addresses.GetArrayLength() > 0)
        {
            var postal = GetString(addresses[0], "postalCode");
            if (postal is { Length: >= 3 })
            {
                postalPrefix = postal[..3].ToUpperInvariant();
            }
        }

        return new PatientRecord(id, versionId, lastUpdated, GetString(resource, "gender"), birthYear, postalPrefix);
    }

    internal static ImmunizationRecord? ParseImmunization(
        JsonElement resource,
        string? fullUrl,
        IReadOnlyDictionary<string, string> fullUrlToPatientId,
        List<string> warnings)
    {
        var id = GetString(resource, "id") ?? IdFromFullUrl(fullUrl);
        if (id is null)
        {
            warnings.Add("Skipped Immunization with no id.");
            return null;
        }

        string? patientRef = null;
        if (resource.TryGetProperty("patient", out var patient))
        {
            patientRef = GetString(patient, "reference");
        }

        var patientId = ResolvePatientReference(patientRef, fullUrlToPatientId);
        if (patientId is null)
        {
            warnings.Add($"Skipped Immunization/{id}: missing or unresolvable patient reference.");
            return null;
        }

        var (versionId, lastUpdated) = ParseMeta(resource);
        var (system, code, display) = PickVaccineCoding(resource);

        DateOnly? occurrence = null;
        var occurrenceText = GetString(resource, "occurrenceDateTime");
        if (occurrenceText is not null)
        {
            occurrence = ParseFhirDate(occurrenceText);
            if (occurrence is null)
            {
                warnings.Add($"Immunization/{id}: occurrence '{occurrenceText}' is not a full date; stored as null.");
            }
        }
        else if (GetString(resource, "occurrenceString") is { } freeText)
        {
            warnings.Add($"Immunization/{id}: free-text occurrence '{freeText}' stored as null.");
        }

        bool? primarySource = null;
        if (resource.TryGetProperty("primarySource", out var ps) &&
            (ps.ValueKind == JsonValueKind.True || ps.ValueKind == JsonValueKind.False))
        {
            primarySource = ps.GetBoolean();
        }

        return new ImmunizationRecord(
            id,
            versionId,
            lastUpdated,
            patientId,
            GetString(resource, "status") ?? "unknown",
            system,
            code,
            display,
            occurrence,
            GetString(resource, "lotNumber"),
            FirstCodingDisplayOrCode(resource, "route"),
            FirstCodingDisplayOrCode(resource, "site"),
            primarySource);
    }

    /// <summary>
    /// Resolves "Patient/123", "Patient/123/_history/2", absolute URLs and urn:uuid references to a patient id.
    /// </summary>
    public static string? ResolvePatientReference(string? reference, IReadOnlyDictionary<string, string>? fullUrlMap = null)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;

        if (reference.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase))
        {
            if (fullUrlMap is not null && fullUrlMap.TryGetValue(reference, out var mapped)) return mapped;
            return reference["urn:uuid:".Length..];
        }

        var parts = reference.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.LastIndexOf(parts, "Patient");
        if (index >= 0 && index + 1 < parts.Length) return parts[index + 1];
        return null;
    }

    /// <summary>
    /// Returns a date for FHIR date or dateTime values with at least day precision, otherwise null.
    /// </summary>
    public static DateOnly? ParseFhirDate(string value)
    {
        if (value.Length < 10) return null;
        return DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static (string? System, string? Code, string? Display) PickVaccineCoding(JsonElement resource)
    {
        if (!resource.TryGetProperty("vaccineCode", out var vaccineCode) ||
            !vaccineCode.TryGetProperty("coding", out var codings) ||
            codings.ValueKind != JsonValueKind.Array ||
            codings.GetArrayLength() == 0)
        {
            return (null, null, GetString(vaccineCode, "text"));
        }

        JsonElement? chosen = null;
        foreach (var preferred in new[] { CvxSystem, SnomedSystem })
        {
            foreach (var coding in codings.EnumerateArray())
            {
                if (GetString(coding, "system") == preferred)
                {
                    chosen = coding;
                    break;
                }
            }
            if (chosen is not null) break;
        }

        var c = chosen ?? codings[0];
        return (GetString(c, "system"), GetString(c, "code"), GetString(c, "display") ?? GetString(vaccineCode, "text"));
    }

    private static string? FirstCodingDisplayOrCode(JsonElement resource, string property)
    {
        if (!resource.TryGetProperty(property, out var concept)) return null;
        if (concept.TryGetProperty("coding", out var codings) &&
            codings.ValueKind == JsonValueKind.Array &&
            codings.GetArrayLength() > 0)
        {
            return GetString(codings[0], "display") ?? GetString(codings[0], "code");
        }
        return GetString(concept, "text");
    }

    private static (string? VersionId, DateTimeOffset? LastUpdated) ParseMeta(JsonElement resource)
    {
        if (!resource.TryGetProperty("meta", out var meta)) return (null, null);
        DateTimeOffset? lastUpdated = null;
        var text = GetString(meta, "lastUpdated");
        if (text is not null &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            lastUpdated = parsed;
        }
        return (GetString(meta, "versionId"), lastUpdated);
    }

    private static string? IdFromFullUrl(string? fullUrl)
    {
        if (fullUrl is null) return null;
        if (fullUrl.StartsWith("urn:uuid:", StringComparison.OrdinalIgnoreCase)) return fullUrl["urn:uuid:".Length..];
        var parts = fullUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : null;
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

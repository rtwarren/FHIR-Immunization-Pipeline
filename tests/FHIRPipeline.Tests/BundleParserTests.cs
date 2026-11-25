namespace FHIRPipeline.Tests;

public class BundleParserTests
{
    [Fact]
    public void Parses_next_link()
    {
        var page = BundleParser.Parse(SampleBundles.Searchset);
        Assert.Equal("https://fhir.example.org/r4?_getpages=abc&_getpagesoffset=2", page.NextLink);
    }

    [Fact]
    public void Patient_is_minimized_to_reporting_attributes()
    {
        var patient = Assert.Single(BundleParser.Parse(SampleBundles.Searchset).Patients);
        Assert.Equal("pat-1", patient.ID);
        Assert.Equal("2", patient.VersionID);
        Assert.Equal("female", patient.Gender);
        Assert.Equal(1987, patient.BirthYear);
        Assert.Equal("X1A", patient.PostalPrefix);

        // The record type has no fields for names, identifiers, full birth date or street address.
        var properties = typeof(PatientRecord).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Name", properties);
        Assert.DoesNotContain("BirthDate", properties);
        Assert.DoesNotContain("Identifier", properties);
    }

    [Fact]
    public void Immunization_fields_are_mapped()
    {
        var imm = BundleParser.Parse(SampleBundles.Searchset).Immunizations.Single(i => i.ID == "imm-1");
        Assert.Equal("3", imm.VersionID);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 15, 20, 0, TimeSpan.FromHours(-6)), imm.LastUpdated);
        Assert.Equal("pat-1", imm.PatientID);
        Assert.Equal("completed", imm.Status);
        Assert.Equal(new DateOnly(2026, 3, 1), imm.OccurrenceDate);
        Assert.Equal("LOT123", imm.LotNumber);
        Assert.Equal("Injection, intramuscular", imm.Route);
        Assert.Equal("Left deltoid", imm.Site);
        Assert.True(imm.PrimarySource);
    }

    [Fact]
    public void CVX_coding_is_preferred_over_other_systems()
    {
        var imm = BundleParser.Parse(SampleBundles.Searchset).Immunizations.Single(i => i.ID == "imm-1");
        Assert.Equal(BundleParser.CVXSystem, imm.VaccineSystem);
        Assert.Equal("208", imm.VaccineCode);
    }

    [Fact]
    public void Falls_back_to_first_coding_and_text_display()
    {
        var imm = BundleParser.Parse(SampleBundles.Searchset).Immunizations.Single(i => i.ID == "imm-2");
        Assert.Equal("MMR", imm.VaccineCode);
        Assert.Equal("Measles, mumps, rubella", imm.VaccineDisplay);
    }

    [Fact]
    public void Free_text_occurrence_becomes_null_with_warning()
    {
        var page = BundleParser.Parse(SampleBundles.Searchset);
        var imm = page.Immunizations.Single(i => i.ID == "imm-2");
        Assert.Null(imm.OccurrenceDate);
        Assert.Contains(page.Warnings, w => w.Contains("imm-2") && w.Contains("free-text"));
    }

    [Fact]
    public void Transaction_bundle_resolves_URN_UUID_references()
    {
        var page = BundleParser.Parse(SampleBundles.Transaction);
        var patient = Assert.Single(page.Patients);
        Assert.Equal("7f3c1a2e-0000-4000-8000-000000000001", patient.ID);
        Assert.Equal(2020, patient.BirthYear);

        var imm = Assert.Single(page.Immunizations);
        Assert.Equal("7f3c1a2e-0000-4000-8000-0000000000aa", imm.ID);
        Assert.Equal(patient.ID, imm.PatientID);
    }

    [Fact]
    public void Partial_date_and_missing_patient_produce_warnings()
    {
        var page = BundleParser.Parse(SampleBundles.Transaction);
        Assert.Null(page.Immunizations.Single().OccurrenceDate);
        Assert.Contains(page.Warnings, w => w.Contains("not a full date"));
        Assert.Contains(page.Warnings, w => w.Contains("Immunization/orphan"));
    }

    [Theory]
    [InlineData("Patient/123", "123")]
    [InlineData("Patient/123/_history/4", "123")]
    [InlineData("https://fhir.example.org/r4/Patient/abc", "abc")]
    [InlineData("urn:uuid:5e1d", "5e1d")]
    [InlineData("Practitioner/9", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Resolves_patient_references(string? reference, string? expected)
    {
        Assert.Equal(expected, BundleParser.ResolvePatientReference(reference));
    }

    [Theory]
    [InlineData("2026-03-01", 2026, 3, 1)]
    [InlineData("2026-03-01T23:59:00+09:00", 2026, 3, 1)]
    public void Parses_full_dates(string value, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), BundleParser.ParseFHIRDate(value));
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("2026-03")]
    [InlineData("not-a-date")]
    public void Rejects_partial_or_invalid_dates(string value)
    {
        Assert.Null(BundleParser.ParseFHIRDate(value));
    }

    [Fact]
    public void Non_bundle_input_is_rejected()
    {
        Assert.Throws<FormatException>(() => BundleParser.Parse("""{ "resourceType": "Patient", "id": "x" }"""));
    }

    [Fact]
    public void Empty_bundle_is_valid()
    {
        var page = BundleParser.Parse("""{ "resourceType": "Bundle", "type": "searchset", "total": 0 }""");
        Assert.Empty(page.Immunizations);
        Assert.Null(page.NextLink);
    }
}

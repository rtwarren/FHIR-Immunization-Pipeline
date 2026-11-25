namespace FHIRPipeline.Tests;

/// <summary>Synthetic FHIR R4 bundles used across tests. No real patient data.</summary>
internal static class SampleBundles
{
    public const string Searchset = """
        {
          "resourceType": "Bundle",
          "type": "searchset",
          "link": [
            { "relation": "self", "url": "https://fhir.example.org/r4/Immunization?_count=2" },
            { "relation": "next", "url": "https://fhir.example.org/r4?_getpages=abc&_getpagesoffset=2" }
          ],
          "entry": [
            {
              "fullUrl": "https://fhir.example.org/r4/Immunization/imm-1",
              "resource": {
                "resourceType": "Immunization",
                "id": "imm-1",
                "meta": { "versionId": "3", "lastUpdated": "2026-03-04T15:20:00.000-06:00" },
                "status": "completed",
                "vaccineCode": {
                  "coding": [
                    { "system": "http://snomed.info/sct", "code": "28531000087107", "display": "COVID-19 vaccine" },
                    { "system": "http://hl7.org/fhir/sid/cvx", "code": "208", "display": "COVID-19, mRNA, LNP-S, PF, 30 mcg/0.3 mL dose" }
                  ]
                },
                "patient": { "reference": "Patient/pat-1" },
                "occurrenceDateTime": "2026-03-01T10:30:00-06:00",
                "primarySource": true,
                "lotNumber": "LOT123",
                "route": { "coding": [ { "code": "IM", "display": "Injection, intramuscular" } ] },
                "site": { "text": "Left deltoid" }
              },
              "search": { "mode": "match" }
            },
            {
              "fullUrl": "https://fhir.example.org/r4/Immunization/imm-2",
              "resource": {
                "resourceType": "Immunization",
                "id": "imm-2",
                "meta": { "versionId": "1", "lastUpdated": "2026-03-05T09:00:00Z" },
                "status": "not-done",
                "vaccineCode": { "coding": [ { "system": "urn:oid:1.2.36.1.2001.1005.17", "code": "MMR" } ], "text": "Measles, mumps, rubella" },
                "patient": { "reference": "https://fhir.example.org/r4/Patient/pat-1/_history/2" },
                "occurrenceString": "Spring 2026"
              },
              "search": { "mode": "match" }
            },
            {
              "fullUrl": "https://fhir.example.org/r4/Patient/pat-1",
              "resource": {
                "resourceType": "Patient",
                "id": "pat-1",
                "meta": { "versionId": "2", "lastUpdated": "2026-02-10T00:00:00Z" },
                "name": [ { "family": "Example", "given": [ "Test" ] } ],
                "identifier": [ { "system": "urn:example:hcn", "value": "123456789" } ],
                "gender": "female",
                "birthDate": "1987-06-15",
                "address": [ { "line": [ "1 Example St" ], "city": "Yellowknife", "postalCode": "x1a 2b3" } ]
              },
              "search": { "mode": "include" }
            }
          ]
        }
        """;

    public const string Transaction = """
        {
          "resourceType": "Bundle",
          "type": "transaction",
          "entry": [
            {
              "fullUrl": "urn:uuid:7f3c1a2e-0000-4000-8000-000000000001",
              "resource": { "resourceType": "Patient", "gender": "male", "birthDate": "2020" }
            },
            {
              "fullUrl": "urn:uuid:7f3c1a2e-0000-4000-8000-0000000000aa",
              "resource": {
                "resourceType": "Immunization",
                "status": "completed",
                "vaccineCode": { "coding": [ { "system": "http://hl7.org/fhir/sid/cvx", "code": "03", "display": "MMR" } ] },
                "patient": { "reference": "urn:uuid:7f3c1a2e-0000-4000-8000-000000000001" },
                "occurrenceDateTime": "2021-05"
              }
            },
            {
              "fullUrl": "urn:uuid:7f3c1a2e-0000-4000-8000-0000000000bb",
              "resource": {
                "resourceType": "Immunization",
                "id": "orphan",
                "status": "completed",
                "vaccineCode": { "text": "Unknown" }
              }
            }
          ]
        }
        """;

    public static string Page(string? next, params (string ID, string LastUpdated)[] immunizations)
    {
        var entries = string.Join(",", immunizations.Select(i => $$"""
            {
              "resource": {
                "resourceType": "Immunization",
                "id": "{{i.ID}}",
                "meta": { "versionId": "1", "lastUpdated": "{{i.LastUpdated}}" },
                "status": "completed",
                "vaccineCode": { "coding": [ { "system": "http://hl7.org/fhir/sid/cvx", "code": "88" } ] },
                "patient": { "reference": "Patient/p-{{i.ID}}" },
                "occurrenceDateTime": "2026-01-15"
              }
            }
            """));
        var link = next is null ? "" : $$"""
            "link": [ { "relation": "next", "url": "{{next}}" } ],
            """;
        return $$"""
            { "resourceType": "Bundle", "type": "searchset", {{link}} "entry": [ {{entries}} ] }
            """;
    }
}

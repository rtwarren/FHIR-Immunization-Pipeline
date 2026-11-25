# fhir-immunization-pipeline

An incremental ETL pipeline that pulls `Immunization` and `Patient` resources from any FHIR R4 server and loads them into SQL Server for reporting.

It is built the way a production public health pipeline needs to be: incremental, safe to re-run, version-aware, and limited to the data reporting actually needs.

## What it does

```
FHIR R4 server                 C# (.NET 8)                        SQL Server
---------------                -----------                        ----------
GET /Immunization      -->     FhirClient                 -->     stg.Patient
  ?_include=                     paging via Bundle.link             stg.Immunization
   Immunization:patient          retry on 429 / 5xx                     |
  &_lastUpdated=gt{wm}         BundleParser                             | core.usp_Merge*
                                 flatten + minimize                     v  (one transaction)
                               PipelineRunner             -->     core.Patient
                                 watermark tracking                core.Immunization
                                                                   etl.Watermark, etl.LoadLog
                                                                        |
                                                                        v
                                                                   reporting views
```

## Design decisions

**Incremental by default.** Each run requests only resources with `_lastUpdated` after the stored watermark. `--full` forces a complete reload.

**Safe to re-run.** Pages are bulk-copied into staging tables. Core tables and the watermark change only in one transaction at the end of the run. If a run fails half way, nothing downstream changes, and the next run starts from the same point.

**Version-aware merges.** A resource can appear on more than one page if it changes while the search is being paged. The merge procedures keep only the newest copy in each batch, and only overwrite a core row when the incoming `meta.lastUpdated` is newer. Replaying an old batch can never overwrite newer data. The SQL test suite checks this directly.

**Data minimization.** The parser keeps what coverage reporting needs and drops the rest at parse time:

| Kept | Dropped |
| --- | --- |
| Patient id, gender, birth year | Names, identifiers (health card numbers), full birth date |
| First 3 characters of postal code | Street address, telecom |
| Vaccine code, date, status, lot, route, site | Free-text notes |

**Explicit FHIR mapping.** Parsing uses `System.Text.Json` directly rather than a full FHIR SDK, so every mapping from FHIR element to column is visible in one file ([`BundleParser.cs`](src/FhirPipeline/BundleParser.cs)). It handles:

- searchset bundles (server search results) and transaction bundles (for example Synthea output)
- patient references as `Patient/123`, `Patient/123/_history/2`, absolute URLs, and `urn:uuid:` with fullUrl resolution
- CVX coding preferred, then SNOMED CT, then the first coding
- `occurrenceDateTime` with timezones; partial dates and `occurrenceString` are stored as null and logged as warnings rather than guessed

## Quick start

### Dry run against the public HAPI test server (no database)

```bash
dotnet run --project src/FhirPipeline -- --dry-run --page-size 50 --max-pages 3
```

The public HAPI server holds test data only. Do not send real patient data to it.

### Full load into SQL Server

```bash
export MSSQL_SA_PASSWORD='<choose a strong password>'
docker compose up -d

# create the database and objects
docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -Q "CREATE DATABASE FhirPipeline"
for f in sql/01_schema.sql sql/02_procedures.sql sql/03_reporting_views.sql; do
  docker compose exec -T sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -b -d FhirPipeline < "$f"
done

export FHIR_PIPELINE_CONNECTION="Server=localhost;Database=FhirPipeline;User Id=sa;Password=$MSSQL_SA_PASSWORD;TrustServerCertificate=true"
dotnet run --project src/FhirPipeline -- --max-pages 5      # first run: full load
dotnet run --project src/FhirPipeline -- --max-pages 5      # later runs: incremental
```

### Options

| Option | Default | Description |
| --- | --- | --- |
| `--fhir-base <url>` | `https://hapi.fhir.org/baseR4` | FHIR R4 base URL |
| `--connection <conn>` | `FHIR_PIPELINE_CONNECTION` | SQL Server connection string |
| `--page-size <n>` | 50 | Resources per page |
| `--max-pages <n>` | 20 | Stop after this many pages |
| `--full` | off | Ignore the watermark |
| `--dry-run` | off | Fetch and parse only, print a summary |

## Database objects

| Object | Purpose |
| --- | --- |
| `stg.Patient`, `stg.Immunization` | Raw rows for the current batch |
| `core.Patient`, `core.Immunization` | Deduplicated, latest version of each resource |
| `core.usp_MergePatient`, `core.usp_MergeImmunization` | Version-aware MERGE from staging |
| `etl.Watermark` | Last `meta.lastUpdated` loaded, per source; never moves backwards |
| `etl.LoadLog` | One row per run: pages, counts, inserts, updates, watermark |
| `core.vw_ImmunizationsByVaccineMonth` | Completed doses and people by vaccine and month |
| `core.vw_ImmunizationsMissingPatient` | Immunizations whose patient was not returned; a data quality check |

All scripts are idempotent and can be re-applied safely.

## Tests

```bash
dotnet test tests/FhirPipeline.Tests
```

- **Unit tests** (xUnit): bundle parsing and minimization, reference resolution, date handling, paging, retry and backoff, watermark logic, CLI validation
- **SQL tests** ([`tests/sql/test_merge.sql`](tests/sql/test_merge.sql)): run in CI against a real SQL Server 2022 container. They cover duplicate collapsing, out-of-order replays, watermark monotonicity, insert and update counts, and the reporting views.

## Project layout

```
src/FhirPipeline/         console app: client, parser, runner, sinks
tests/FhirPipeline.Tests/ xUnit tests with synthetic FHIR bundles
sql/                      schema, merge procedures, reporting views
tests/sql/                SQL integration tests
```

All data in this repository is synthetic.

## License

MIT

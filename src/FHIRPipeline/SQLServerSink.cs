using System.Data;
using Microsoft.Data.SqlClient;

namespace FHIRPipeline;

/// <summary>
/// Bulk-loads each page into staging tables, then merges staging into core tables and
/// advances the watermark in a single transaction at the end of the run.
/// If the run fails part way, core tables and the watermark are untouched and the next
/// run picks up from the same point.
/// </summary>
public sealed class SQLServerSink : IRecordSink, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly string _source;
    private SqlConnection? _connection;
    private Guid _batchID;

    public SQLServerSink(string connectionString, string source = "fhir:Immunization")
    {
        _connectionString = connectionString;
        _source = source;
    }

    public Guid BatchID => _batchID;

    public async Task<DateTimeOffset?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = new SqlCommand(
            "SELECT LastUpdated FROM etl.Watermark WHERE Source = @Source;", connection);
        cmd.Parameters.Add("@Source", SqlDbType.NVarChar, 100).Value = _source;
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is DateTimeOffset stamp ? stamp : null;
    }

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        _batchID = Guid.NewGuid();
        await using var cmd = new SqlCommand(
            "DELETE FROM stg.Patient; DELETE FROM stg.Immunization; " +
            "INSERT INTO etl.LoadLog (BatchID, Source) VALUES (@BatchID, @Source);",
            connection);
        cmd.Parameters.Add("@BatchID", SqlDbType.UniqueIdentifier).Value = _batchID;
        cmd.Parameters.Add("@Source", SqlDbType.NVarChar, 100).Value = _source;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(BundlePage page, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (page.Patients.Count > 0)
        {
            await BulkCopyAsync(connection, "stg.Patient", ToPatientTable(page.Patients, _batchID), cancellationToken)
                .ConfigureAwait(false);
        }
        if (page.Immunizations.Count > 0)
        {
            await BulkCopyAsync(connection, "stg.Immunization", ToImmunizationTable(page.Immunizations, _batchID), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task CompleteAsync(RunSummary summary, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecProcAsync(connection, tx, "core.usp_MergePatient", cancellationToken).ConfigureAwait(false);
            await ExecProcAsync(connection, tx, "core.usp_MergeImmunization", cancellationToken).ConfigureAwait(false);

            await using (var cmd = new SqlCommand("etl.usp_CompleteLoad", connection, tx))
            {
                cmd.CommandType = CommandType.StoredProcedure;
                cmd.Parameters.Add("@BatchID", SqlDbType.UniqueIdentifier).Value = _batchID;
                cmd.Parameters.Add("@Source", SqlDbType.NVarChar, 100).Value = _source;
                cmd.Parameters.Add("@Watermark", SqlDbType.DateTimeOffset).Value =
                    summary.Watermark is { } w ? (object)w : DBNull.Value;
                cmd.Parameters.Add("@Pages", SqlDbType.Int).Value = summary.Pages;
                cmd.Parameters.Add("@Patients", SqlDbType.Int).Value = summary.Patients;
                cmd.Parameters.Add("@Immunizations", SqlDbType.Int).Value = summary.Immunizations;
                cmd.Parameters.Add("@Warnings", SqlDbType.Int).Value = summary.Warnings;
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    internal static DataTable ToPatientTable(IEnumerable<PatientRecord> patients, Guid batchID)
    {
        var table = new DataTable();
        table.Columns.Add("BatchID", typeof(Guid));
        table.Columns.Add("PatientID", typeof(string));
        table.Columns.Add("VersionID", typeof(string));
        table.Columns.Add("LastUpdated", typeof(DateTimeOffset));
        table.Columns.Add("Gender", typeof(string));
        table.Columns.Add("BirthYear", typeof(short));
        table.Columns.Add("PostalPrefix", typeof(string));
        foreach (var p in patients)
        {
            table.Rows.Add(
                batchID,
                p.ID,
                (object?)p.VersionID ?? DBNull.Value,
                p.LastUpdated is { } lu ? (object)lu : DBNull.Value,
                (object?)p.Gender ?? DBNull.Value,
                p.BirthYear is { } by ? (object)(short)by : DBNull.Value,
                (object?)p.PostalPrefix ?? DBNull.Value);
        }
        return table;
    }

    internal static DataTable ToImmunizationTable(IEnumerable<ImmunizationRecord> immunizations, Guid batchID)
    {
        var table = new DataTable();
        table.Columns.Add("BatchID", typeof(Guid));
        table.Columns.Add("ImmunizationID", typeof(string));
        table.Columns.Add("VersionID", typeof(string));
        table.Columns.Add("LastUpdated", typeof(DateTimeOffset));
        table.Columns.Add("PatientID", typeof(string));
        table.Columns.Add("Status", typeof(string));
        table.Columns.Add("VaccineSystem", typeof(string));
        table.Columns.Add("VaccineCode", typeof(string));
        table.Columns.Add("VaccineDisplay", typeof(string));
        table.Columns.Add("OccurrenceDate", typeof(DateTime));
        table.Columns.Add("LotNumber", typeof(string));
        table.Columns.Add("Route", typeof(string));
        table.Columns.Add("Site", typeof(string));
        table.Columns.Add("PrimarySource", typeof(bool));
        foreach (var i in immunizations)
        {
            table.Rows.Add(
                batchID,
                i.ID,
                (object?)i.VersionID ?? DBNull.Value,
                i.LastUpdated is { } lu ? (object)lu : DBNull.Value,
                i.PatientID,
                i.Status,
                (object?)i.VaccineSystem ?? DBNull.Value,
                (object?)i.VaccineCode ?? DBNull.Value,
                (object?)i.VaccineDisplay ?? DBNull.Value,
                i.OccurrenceDate is { } od ? (object)od.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                (object?)i.LotNumber ?? DBNull.Value,
                (object?)i.Route ?? DBNull.Value,
                (object?)i.Site ?? DBNull.Value,
                i.PrimarySource is { } ps ? (object)ps : DBNull.Value);
        }
        return table;
    }

    private async Task<SqlConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            _connection = new SqlConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        return _connection;
    }

    private static async Task BulkCopyAsync(SqlConnection connection, string tableName, DataTable data, CancellationToken cancellationToken)
    {
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = tableName };
        foreach (DataColumn column in data.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }
        await bulk.WriteToServerAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecProcAsync(SqlConnection connection, SqlTransaction tx, string proc, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(proc, connection, tx) { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.Add("@BatchID", SqlDbType.UniqueIdentifier).Value = _batchID;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

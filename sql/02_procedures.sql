/*
  Merge procedures.

  Each procedure:
    1. keeps only the newest copy of each resource in the batch (a resource can
       appear on more than one page if it changes during paging)
    2. inserts new resources
    3. updates existing resources only when the incoming copy is newer, so replaying
       an old batch never overwrites newer data
*/
SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE core.usp_MergePatient
    @BatchId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @changes TABLE (Action nvarchar(10) NOT NULL);

    WITH latest AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY PatientId
                                  ORDER BY LastUpdated DESC, VersionId DESC) AS rn
        FROM stg.Patient
        WHERE BatchId = @BatchId
    )
    MERGE core.Patient AS tgt
    USING (SELECT * FROM latest WHERE rn = 1) AS src
        ON tgt.PatientId = src.PatientId
    WHEN MATCHED AND (tgt.LastUpdated IS NULL OR src.LastUpdated > tgt.LastUpdated) THEN
        UPDATE SET VersionId    = src.VersionId,
                   LastUpdated  = src.LastUpdated,
                   Gender       = src.Gender,
                   BirthYear    = src.BirthYear,
                   PostalPrefix = src.PostalPrefix,
                   UpdatedAt    = SYSUTCDATETIME(),
                   LastBatchId  = @BatchId
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (PatientId, VersionId, LastUpdated, Gender, BirthYear, PostalPrefix, LastBatchId)
        VALUES (src.PatientId, src.VersionId, src.LastUpdated, src.Gender, src.BirthYear, src.PostalPrefix, @BatchId)
    OUTPUT $action INTO @changes;

    SELECT
        SUM(CASE WHEN Action = N'INSERT' THEN 1 ELSE 0 END) AS Inserted,
        SUM(CASE WHEN Action = N'UPDATE' THEN 1 ELSE 0 END) AS Updated
    FROM @changes;
END;
GO

CREATE OR ALTER PROCEDURE core.usp_MergeImmunization
    @BatchId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @changes TABLE (Action nvarchar(10) NOT NULL);

    WITH latest AS
    (
        SELECT *,
               ROW_NUMBER() OVER (PARTITION BY ImmunizationId
                                  ORDER BY LastUpdated DESC, VersionId DESC) AS rn
        FROM stg.Immunization
        WHERE BatchId = @BatchId
    )
    MERGE core.Immunization AS tgt
    USING (SELECT * FROM latest WHERE rn = 1) AS src
        ON tgt.ImmunizationId = src.ImmunizationId
    WHEN MATCHED AND (tgt.LastUpdated IS NULL OR src.LastUpdated > tgt.LastUpdated) THEN
        UPDATE SET VersionId      = src.VersionId,
                   LastUpdated    = src.LastUpdated,
                   PatientId      = src.PatientId,
                   Status         = src.Status,
                   VaccineSystem  = src.VaccineSystem,
                   VaccineCode    = src.VaccineCode,
                   VaccineDisplay = src.VaccineDisplay,
                   OccurrenceDate = src.OccurrenceDate,
                   LotNumber      = src.LotNumber,
                   Route          = src.Route,
                   Site           = src.Site,
                   PrimarySource  = src.PrimarySource,
                   UpdatedAt      = SYSUTCDATETIME(),
                   LastBatchId    = @BatchId
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ImmunizationId, VersionId, LastUpdated, PatientId, Status, VaccineSystem, VaccineCode,
                VaccineDisplay, OccurrenceDate, LotNumber, Route, Site, PrimarySource, LastBatchId)
        VALUES (src.ImmunizationId, src.VersionId, src.LastUpdated, src.PatientId, src.Status, src.VaccineSystem,
                src.VaccineCode, src.VaccineDisplay, src.OccurrenceDate, src.LotNumber, src.Route, src.Site,
                src.PrimarySource, @BatchId)
    OUTPUT $action INTO @changes;

    DECLARE @inserted int = (SELECT COUNT(*) FROM @changes WHERE Action = N'INSERT');
    DECLARE @updated  int = (SELECT COUNT(*) FROM @changes WHERE Action = N'UPDATE');

    UPDATE etl.LoadLog SET Inserted = @inserted, Updated = @updated WHERE BatchId = @BatchId;

    SELECT @inserted AS Inserted, @updated AS Updated;
END;
GO

CREATE OR ALTER PROCEDURE etl.usp_CompleteLoad
    @BatchId       uniqueidentifier,
    @Source        nvarchar(100),
    @Watermark     datetimeoffset(3) = NULL,
    @Pages         int,
    @Patients      int,
    @Immunizations int,
    @Warnings      int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Watermark IS NOT NULL
    BEGIN
        MERGE etl.Watermark AS tgt
        USING (SELECT @Source AS Source, @Watermark AS LastUpdated) AS src
            ON tgt.Source = src.Source
        WHEN MATCHED AND src.LastUpdated > tgt.LastUpdated THEN
            UPDATE SET LastUpdated = src.LastUpdated, UpdatedAt = SYSUTCDATETIME()
        WHEN NOT MATCHED THEN
            INSERT (Source, LastUpdated) VALUES (src.Source, src.LastUpdated);
    END;

    UPDATE etl.LoadLog
    SET CompletedAt   = SYSUTCDATETIME(),
        Pages         = @Pages,
        Patients      = @Patients,
        Immunizations = @Immunizations,
        Warnings      = @Warnings,
        Watermark     = @Watermark
    WHERE BatchId = @BatchId;
END;
GO

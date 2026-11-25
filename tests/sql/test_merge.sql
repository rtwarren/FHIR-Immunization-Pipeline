/*
  Integration tests for the merge procedures. Run after sql/01-03 against an empty database.
  Any failed assertion raises an error, which fails the CI job (sqlcmd -b).
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @b1 uniqueidentifier = NEWID();
DECLARE @b2 uniqueidentifier = NEWID();
DECLARE @b3 uniqueidentifier = NEWID();

/* ---------- Batch 1: initial load, with a duplicate across pages ---------- */
INSERT etl.LoadLog (BatchId, Source) VALUES (@b1, N'test');

INSERT stg.Patient (BatchId, PatientId, VersionId, LastUpdated, Gender, BirthYear, PostalPrefix)
VALUES (@b1, N'p1', N'1', '2026-01-01T00:00:00Z', N'female', 1990, N'X1A'),
       (@b1, N'p2', N'1', '2026-01-01T00:00:00Z', N'male',   2019, N'X0E');

INSERT stg.Immunization (BatchId, ImmunizationId, VersionId, LastUpdated, PatientId, Status, VaccineCode, VaccineDisplay, OccurrenceDate)
VALUES (@b1, N'i1', N'1', '2026-01-02T00:00:00Z', N'p1', N'completed',      N'208', N'COVID-19 mRNA', '2026-01-02'),
       (@b1, N'i2', N'1', '2026-01-02T00:00:00Z', N'p2', N'completed',      N'03',  N'MMR',           '2026-01-03'),
       -- same resource seen again on a later page, now a newer version
       (@b1, N'i2', N'2', '2026-01-05T00:00:00Z', N'p2', N'entered-in-error', N'03', N'MMR',          '2026-01-03');

EXEC core.usp_MergePatient @BatchId = @b1;
EXEC core.usp_MergeImmunization @BatchId = @b1;
EXEC etl.usp_CompleteLoad @BatchId = @b1, @Source = N'test', @Watermark = '2026-01-05T00:00:00Z',
     @Pages = 2, @Patients = 2, @Immunizations = 3, @Warnings = 0;

IF (SELECT COUNT(*) FROM core.Patient) <> 2
    THROW 50001, 'Batch 1: expected 2 patients', 1;
IF (SELECT COUNT(*) FROM core.Immunization) <> 2
    THROW 50002, 'Batch 1: duplicate immunization was not collapsed', 1;
IF (SELECT Status FROM core.Immunization WHERE ImmunizationId = N'i2') <> N'entered-in-error'
    THROW 50003, 'Batch 1: newest version of i2 was not kept', 1;
IF (SELECT Inserted FROM etl.LoadLog WHERE BatchId = @b1) <> 2
    THROW 50004, 'Batch 1: load log insert count wrong', 1;
IF (SELECT LastUpdated FROM etl.Watermark WHERE Source = N'test') <> '2026-01-05T00:00:00Z'
    THROW 50005, 'Batch 1: watermark not set', 1;

/* ---------- Batch 2: replay of an OLD version must not overwrite ---------- */
DELETE stg.Patient; DELETE stg.Immunization;
INSERT etl.LoadLog (BatchId, Source) VALUES (@b2, N'test');

INSERT stg.Immunization (BatchId, ImmunizationId, VersionId, LastUpdated, PatientId, Status, VaccineCode, OccurrenceDate)
VALUES (@b2, N'i2', N'1', '2026-01-02T00:00:00Z', N'p2', N'completed', N'03', '2026-01-03');

EXEC core.usp_MergeImmunization @BatchId = @b2;
EXEC etl.usp_CompleteLoad @BatchId = @b2, @Source = N'test', @Watermark = '2026-01-02T00:00:00Z',
     @Pages = 1, @Patients = 0, @Immunizations = 1, @Warnings = 0;

IF (SELECT Status FROM core.Immunization WHERE ImmunizationId = N'i2') <> N'entered-in-error'
    THROW 50006, 'Batch 2: older version overwrote newer data', 1;
IF (SELECT LastUpdated FROM etl.Watermark WHERE Source = N'test') <> '2026-01-05T00:00:00Z'
    THROW 50007, 'Batch 2: watermark moved backwards', 1;

/* ---------- Batch 3: newer update and a new resource ---------- */
DELETE stg.Patient; DELETE stg.Immunization;
INSERT etl.LoadLog (BatchId, Source) VALUES (@b3, N'test');

INSERT stg.Immunization (BatchId, ImmunizationId, VersionId, LastUpdated, PatientId, Status, VaccineCode, OccurrenceDate)
VALUES (@b3, N'i1', N'2', '2026-02-01T00:00:00Z', N'p1', N'completed', N'208', '2026-01-02'),
       (@b3, N'i3', N'1', '2026-02-01T00:00:00Z', N'p9', N'completed', N'20',  '2026-01-20');

EXEC core.usp_MergeImmunization @BatchId = @b3;
EXEC etl.usp_CompleteLoad @BatchId = @b3, @Source = N'test', @Watermark = '2026-02-01T00:00:00Z',
     @Pages = 1, @Patients = 0, @Immunizations = 2, @Warnings = 0;

IF (SELECT Inserted FROM etl.LoadLog WHERE BatchId = @b3) <> 1 OR (SELECT Updated FROM etl.LoadLog WHERE BatchId = @b3) <> 1
    THROW 50008, 'Batch 3: expected 1 insert and 1 update', 1;
IF (SELECT VersionId FROM core.Immunization WHERE ImmunizationId = N'i1') <> N'2'
    THROW 50009, 'Batch 3: i1 not updated to version 2', 1;
IF NOT EXISTS (SELECT 1 FROM core.vw_ImmunizationsMissingPatient WHERE ImmunizationId = N'i3')
    THROW 50010, 'Batch 3: missing-patient view did not flag i3', 1;
-- i1 and i3 are completed; i2 was entered in error and must be excluded.
IF (SELECT SUM(Doses) FROM core.vw_ImmunizationsByVaccineMonth) <> 2
    THROW 50011, 'Views: expected 2 completed doses', 1;

PRINT 'All SQL tests passed.';

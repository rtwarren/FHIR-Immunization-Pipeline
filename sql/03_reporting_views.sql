/*
  Reporting views built on the core tables.
*/
SET NOCOUNT ON;
GO

CREATE OR ALTER VIEW core.vw_ImmunizationsByVaccineMonth
AS
SELECT
    DATEFROMPARTS(YEAR(i.OccurrenceDate), MONTH(i.OccurrenceDate), 1) AS MonthStart,
    COALESCE(i.VaccineDisplay, i.VaccineCode, N'(uncoded)')            AS Vaccine,
    i.VaccineCode,
    COUNT(*)                                                          AS Doses,
    COUNT(DISTINCT i.PatientID)                                       AS People
FROM core.Immunization AS i
WHERE i.Status = N'completed'
  AND i.OccurrenceDate IS NOT NULL
GROUP BY
    DATEFROMPARTS(YEAR(i.OccurrenceDate), MONTH(i.OccurrenceDate), 1),
    COALESCE(i.VaccineDisplay, i.VaccineCode, N'(uncoded)'),
    i.VaccineCode;
GO

CREATE OR ALTER VIEW core.vw_ImmunizationsMissingPatient
AS
-- Immunizations whose patient was not returned by _include; usually a permissions
-- or paging issue on the source server, worth monitoring.
SELECT i.ImmunizationID, i.PatientID, i.LastUpdated
FROM core.Immunization AS i
WHERE NOT EXISTS (SELECT 1 FROM core.Patient AS p WHERE p.PatientID = i.PatientID);
GO

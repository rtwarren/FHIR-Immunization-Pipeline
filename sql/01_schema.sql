/*
  Schema for the FHIR immunization pipeline.

  stg   raw rows for the current batch, replaced every run
  core  deduplicated, version-aware tables for reporting
  etl   watermark and load log
*/
SET NOCOUNT ON;
GO

IF SCHEMA_ID(N'stg') IS NULL EXEC (N'CREATE SCHEMA stg AUTHORIZATION dbo;');
IF SCHEMA_ID(N'core') IS NULL EXEC (N'CREATE SCHEMA core AUTHORIZATION dbo;');
IF SCHEMA_ID(N'etl') IS NULL EXEC (N'CREATE SCHEMA etl AUTHORIZATION dbo;');
GO

/* ---------- staging ---------- */

IF OBJECT_ID(N'stg.Patient', N'U') IS NULL
CREATE TABLE stg.Patient
(
    BatchID       uniqueidentifier NOT NULL,
    PatientID     nvarchar(64)     NOT NULL,
    VersionID     nvarchar(64)     NULL,
    LastUpdated   datetimeoffset(3) NULL,
    Gender        nvarchar(16)     NULL,
    BirthYear     smallint         NULL,
    PostalPrefix  nvarchar(3)      NULL
);
GO

IF OBJECT_ID(N'stg.Immunization', N'U') IS NULL
CREATE TABLE stg.Immunization
(
    BatchID         uniqueidentifier NOT NULL,
    ImmunizationID  nvarchar(64)     NOT NULL,
    VersionID       nvarchar(64)     NULL,
    LastUpdated     datetimeoffset(3) NULL,
    PatientID       nvarchar(64)     NOT NULL,
    Status          nvarchar(32)     NOT NULL,
    VaccineSystem   nvarchar(256)    NULL,
    VaccineCode     nvarchar(64)     NULL,
    VaccineDisplay  nvarchar(512)    NULL,
    OccurrenceDate  date             NULL,
    LotNumber       nvarchar(64)     NULL,
    Route           nvarchar(128)    NULL,
    Site            nvarchar(128)    NULL,
    PrimarySource   bit              NULL
);
GO

/* ---------- core ---------- */

IF OBJECT_ID(N'core.Patient', N'U') IS NULL
CREATE TABLE core.Patient
(
    PatientKey    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_core_Patient PRIMARY KEY,
    PatientID     nvarchar(64)      NOT NULL CONSTRAINT UQ_core_Patient_PatientId UNIQUE,
    VersionID     nvarchar(64)      NULL,
    LastUpdated   datetimeoffset(3) NULL,
    Gender        nvarchar(16)      NULL,
    BirthYear     smallint          NULL,
    PostalPrefix  nvarchar(3)       NULL,
    InsertedAt    datetime2(3)      NOT NULL CONSTRAINT DF_core_Patient_InsertedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt     datetime2(3)      NULL,
    LastBatchID   uniqueidentifier  NOT NULL
);
GO

IF OBJECT_ID(N'core.Immunization', N'U') IS NULL
CREATE TABLE core.Immunization
(
    ImmunizationKey int IDENTITY(1,1) NOT NULL CONSTRAINT PK_core_Immunization PRIMARY KEY,
    ImmunizationID  nvarchar(64)      NOT NULL CONSTRAINT UQ_core_Immunization_ImmunizationId UNIQUE,
    VersionID       nvarchar(64)      NULL,
    LastUpdated     datetimeoffset(3) NULL,
    PatientID       nvarchar(64)      NOT NULL,
    Status          nvarchar(32)      NOT NULL,
    VaccineSystem   nvarchar(256)     NULL,
    VaccineCode     nvarchar(64)      NULL,
    VaccineDisplay  nvarchar(512)     NULL,
    OccurrenceDate  date              NULL,
    LotNumber       nvarchar(64)      NULL,
    Route           nvarchar(128)     NULL,
    Site            nvarchar(128)     NULL,
    PrimarySource   bit               NULL,
    InsertedAt      datetime2(3)      NOT NULL CONSTRAINT DF_core_Immunization_InsertedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt       datetime2(3)      NULL,
    LastBatchID     uniqueidentifier  NOT NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_core_Immunization_PatientId')
CREATE INDEX IX_core_Immunization_PatientId ON core.Immunization (PatientID) INCLUDE (VaccineCode, OccurrenceDate, Status);
GO

/* ---------- etl ---------- */

IF OBJECT_ID(N'etl.Watermark', N'U') IS NULL
CREATE TABLE etl.Watermark
(
    Source       nvarchar(100)     NOT NULL CONSTRAINT PK_etl_Watermark PRIMARY KEY,
    LastUpdated  datetimeoffset(3) NOT NULL,
    UpdatedAt    datetime2(3)      NOT NULL CONSTRAINT DF_etl_Watermark_UpdatedAt DEFAULT SYSUTCDATETIME()
);
GO

IF OBJECT_ID(N'etl.LoadLog', N'U') IS NULL
CREATE TABLE etl.LoadLog
(
    BatchID        uniqueidentifier NOT NULL CONSTRAINT PK_etl_LoadLog PRIMARY KEY,
    Source         nvarchar(100)    NOT NULL,
    StartedAt      datetime2(3)     NOT NULL CONSTRAINT DF_etl_LoadLog_StartedAt DEFAULT SYSUTCDATETIME(),
    CompletedAt    datetime2(3)     NULL,
    Pages          int              NULL,
    Patients       int              NULL,
    Immunizations  int              NULL,
    Warnings       int              NULL,
    Inserted       int              NULL,
    Updated        int              NULL,
    Watermark      datetimeoffset(3) NULL
);
GO

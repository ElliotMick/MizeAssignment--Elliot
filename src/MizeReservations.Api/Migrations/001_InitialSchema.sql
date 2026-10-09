/*
    001_InitialSchema.sql

    Hand-written schema for the reservation ingestion service (Dapper has no migrations of its own).
    Every object is guarded, so the script is safe to run more than once.

    Applied automatically by DatabaseMigrator when Database:ApplyMigrationsOnStartup is true (Development),
    or by hand:
        sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "IF DB_ID('MizeReservations') IS NULL CREATE DATABASE MizeReservations"
        sqlcmd -S "(localdb)\MSSQLLocalDB" -d MizeReservations -i 001_InitialSchema.sql

    Identifier columns use a binary collation (Latin1_General_100_BIN2): supplier and reservation IDs are opaque,
    case-sensitive strings, so "R-1" and "r-1" are two different reservations.
*/

-- One row per (supplier, reservation). The composite primary key is what makes ingest idempotent:
-- the database itself refuses a second row for the same supplier + reservation.
IF OBJECT_ID(N'dbo.Reservations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Reservations
    (
        SupplierId        NVARCHAR(100)  COLLATE Latin1_General_100_BIN2 NOT NULL,
        ReservationId     NVARCHAR(100)  COLLATE Latin1_General_100_BIN2 NOT NULL,
        RoomId            NVARCHAR(100)  COLLATE Latin1_General_100_BIN2 NOT NULL,
        CheckInUtc        DATETIME2(7)   NOT NULL,
        CheckOutUtc       DATETIME2(7)   NOT NULL,
        Price             DECIMAL(18, 2) NOT NULL,
        UpdatedAtUtc      DATETIME2(7)   NOT NULL, -- the supplier's version timestamp
        CreatedAtUtc      DATETIME2(7)   NOT NULL, -- server time of the first write
        LastModifiedAtUtc DATETIME2(7)   NOT NULL, -- server time of the latest write
        RowVersion        ROWVERSION     NOT NULL, -- optimistic concurrency token, changes on every write

        CONSTRAINT PK_Reservations PRIMARY KEY CLUSTERED (SupplierId, ReservationId),
        CONSTRAINT CK_Reservations_StayDates CHECK (CheckOutUtc > CheckInUtc),
        CONSTRAINT CK_Reservations_Price CHECK (Price >= 0)
    );
END;

-- Sliding-window log of the ingest requests each supplier was allowed to make (used for throttling).
-- Entries older than the window are deleted on every request, so it never holds more than PermitLimit rows per supplier.
IF OBJECT_ID(N'dbo.SupplierRequestLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupplierRequestLog
    (
        Id             BIGINT IDENTITY(1, 1) NOT NULL,
        SupplierId     NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        RequestedAtUtc DATETIME2(7)  NOT NULL,

        CONSTRAINT PK_SupplierRequestLog PRIMARY KEY CLUSTERED (SupplierId, RequestedAtUtc, Id)
    );
END;

-- Per-supplier counters behind GET /api/reservations/stats/{supplierId}.
IF OBJECT_ID(N'dbo.SupplierStats', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupplierStats
    (
        SupplierId        NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
        CreatedCount      BIGINT        NOT NULL CONSTRAINT DF_SupplierStats_CreatedCount   DEFAULT (0),
        UpdatedCount      BIGINT        NOT NULL CONSTRAINT DF_SupplierStats_UpdatedCount   DEFAULT (0),
        UnchangedCount    BIGINT        NOT NULL CONSTRAINT DF_SupplierStats_UnchangedCount DEFAULT (0),
        StaleCount        BIGINT        NOT NULL CONSTRAINT DF_SupplierStats_StaleCount     DEFAULT (0),
        ThrottledCount    BIGINT        NOT NULL CONSTRAINT DF_SupplierStats_ThrottledCount DEFAULT (0),
        LastActivityAtUtc DATETIME2(7)  NOT NULL,

        CONSTRAINT PK_SupplierStats PRIMARY KEY CLUSTERED (SupplierId)
    );
END;

-- V001 — Initial schema
-- Applied by MigrationRunner on first volume open.
-- Structural DDL only; initial Settings rows are inserted at volume-creation time.

-- ===== Schema versioning =====
CREATE TABLE SchemaVersions (
    Version       INTEGER PRIMARY KEY,
    AppliedUtc    TEXT    NOT NULL,
    Description   TEXT    NOT NULL
);

-- ===== Files: the user-facing tree =====
CREATE TABLE Files (
    FileID        TEXT    PRIMARY KEY,
    ParentID      TEXT    REFERENCES Files(FileID),
    IsFolder      INTEGER NOT NULL DEFAULT 0,
    IsSymlink     INTEGER NOT NULL DEFAULT 0,
    SymlinkTarget TEXT,
    Name          TEXT    NOT NULL,
    Extension     TEXT,
    MimeType      TEXT,
    VirtualPath   TEXT    NOT NULL,
    SizeBytes     INTEGER NOT NULL DEFAULT 0,
    CreatedUtc    TEXT    NOT NULL,
    ModifiedUtc   TEXT    NOT NULL,
    AddedUtc      TEXT    NOT NULL,
    BlobID        TEXT    REFERENCES Blobs(BlobID)
);

CREATE UNIQUE INDEX IX_Files_Parent_Name
    ON Files (COALESCE(ParentID, ''), Name);

CREATE INDEX IX_Files_BlobID
    ON Files (BlobID)
    WHERE BlobID IS NOT NULL;

CREATE INDEX IX_Files_ParentID
    ON Files (ParentID);

-- ===== Blobs: encrypted file payloads =====
CREATE TABLE Blobs (
    BlobID            TEXT    PRIMARY KEY,
    EncryptedSize     INTEGER NOT NULL,
    PlaintextSize     INTEGER NOT NULL,
    PlaintextSHA256   TEXT    NOT NULL,
    EncryptedXXHash   TEXT    NOT NULL,
    Compression       TEXT,
    BlobPath          TEXT    NOT NULL,
    CreatedUtc        TEXT    NOT NULL,
    SoftDeletedUtc    TEXT,
    PurgeAfterUtc     TEXT
);

CREATE INDEX IX_Blobs_PlaintextSHA256
    ON Blobs (PlaintextSHA256);

CREATE INDEX IX_Blobs_PurgeAfterUtc
    ON Blobs (PurgeAfterUtc)
    WHERE PurgeAfterUtc IS NOT NULL;

-- ===== Providers (tails) =====
CREATE TABLE Providers (
    ProviderID              TEXT    PRIMARY KEY,
    ProviderType            TEXT    NOT NULL,
    DisplayName             TEXT    NOT NULL,
    EncryptedToken          BLOB,
    TokenNonce              TEXT,
    EncryptedClientSecret   BLOB,
    ClientSecretNonce       TEXT,
    ClientId                TEXT,
    ProviderConfig          TEXT,
    HealthStatus            TEXT    NOT NULL,
    LastHealthCheckUtc      TEXT,
    AddedUtc                TEXT    NOT NULL,
    IsActive                INTEGER NOT NULL DEFAULT 1
);

-- ===== TailUploads: per-file per-tail upload status =====
CREATE TABLE TailUploads (
    FileID          TEXT    NOT NULL REFERENCES Files(FileID)     ON DELETE CASCADE,
    ProviderID      TEXT    NOT NULL REFERENCES Providers(ProviderID),
    Status          TEXT    NOT NULL,
    RemoteId        TEXT,
    QueuedUtc       TEXT    NOT NULL,
    UploadedUtc     TEXT,
    LastAttemptUtc  TEXT,
    AttemptCount    INTEGER NOT NULL DEFAULT 0,
    LastError       TEXT,
    PRIMARY KEY (FileID, ProviderID)
);

CREATE INDEX IX_TailUploads_PendingByProvider
    ON TailUploads (ProviderID, Status)
    WHERE Status != 'UPLOADED';

-- ===== UploadSessions: in-flight resumable upload state =====
CREATE TABLE UploadSessions (
    FileID              TEXT    NOT NULL REFERENCES Files(FileID),
    ProviderID          TEXT    NOT NULL REFERENCES Providers(ProviderID),
    SessionUri          TEXT    NOT NULL,
    SessionExpiresUtc   TEXT    NOT NULL,
    BytesUploaded       INTEGER NOT NULL,
    TotalBytes          INTEGER NOT NULL,
    LastActivityUtc     TEXT    NOT NULL,
    PRIMARY KEY (FileID, ProviderID)
);

-- ===== WAL: crash-recovery state machine =====
CREATE TABLE WAL (
    WALID       TEXT    PRIMARY KEY,
    Operation   TEXT    NOT NULL,
    Phase       TEXT    NOT NULL,
    StartedUtc  TEXT    NOT NULL,
    UpdatedUtc  TEXT    NOT NULL,
    Payload     TEXT    NOT NULL
);

-- ===== BackgroundFailures: persisted notification queue =====
CREATE TABLE BackgroundFailures (
    FailureID    TEXT    PRIMARY KEY,
    OccurredUtc  TEXT    NOT NULL,
    Source       TEXT    NOT NULL,
    ErrorCode    TEXT    NOT NULL,
    Message      TEXT    NOT NULL,
    Metadata     TEXT,
    Acknowledged INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IX_BackgroundFailures_Unacked
    ON BackgroundFailures (OccurredUtc DESC)
    WHERE Acknowledged = 0;

-- ===== ActivityLog: user-facing audit trail =====
CREATE TABLE ActivityLog (
    EntryID      TEXT    PRIMARY KEY,
    OccurredUtc  TEXT    NOT NULL,
    Category     TEXT    NOT NULL,
    Summary      TEXT    NOT NULL,
    Detail       TEXT
);

CREATE INDEX IX_ActivityLog_OccurredUtc
    ON ActivityLog (OccurredUtc DESC);

-- ===== DeleteLog: append-only audit trail for hard-deletes =====
CREATE TABLE DeleteLog (
    LogID       TEXT    PRIMARY KEY,
    DeletedAt   TEXT    NOT NULL,
    FileID      TEXT    NOT NULL,
    Name        TEXT    NOT NULL,
    VirtualPath TEXT    NOT NULL,
    IsFolder    INTEGER NOT NULL,
    Trigger     TEXT    NOT NULL
);

-- ===== Settings: key-value config =====
CREATE TABLE Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

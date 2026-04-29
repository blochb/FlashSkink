namespace FlashSkink.Core.Abstractions.Results;

/// <summary>
/// Discriminated set of all failure modes the application can produce.
/// Callers switch on this value to decide recovery strategy.
/// <para>
/// Logging convention: <see cref="ErrorContext.Metadata"/> keys must never match
/// *Token, *Key, *Password, *Secret, *Mnemonic, or *Phrase (principle 26).
/// </para>
/// </summary>
public enum ErrorCode
{
    /// <summary>An unexpected failure that was not anticipated by the error model.</summary>
    Unknown = 0,

    // ── Control flow ──────────────────────────────────────────────────────────

    /// <summary>The operation was cancelled via a <see cref="System.Threading.CancellationToken"/>.</summary>
    Cancelled,

    /// <summary>The operation exceeded its allowed time budget.</summary>
    Timeout,

    // ── Volume ────────────────────────────────────────────────────────────────

    /// <summary>No volume was found at the expected path on the skink.</summary>
    VolumeNotFound,

    /// <summary>A volume is already open and a second concurrent open was attempted.</summary>
    VolumeAlreadyOpen,

    /// <summary>The volume's on-disk structures are corrupt and cannot be read.</summary>
    VolumeCorrupt,

    /// <summary>The volume is mounted read-only; the operation requires write access.</summary>
    VolumeReadOnly,

    /// <summary>A volume already exists at the target path; creation was not attempted.</summary>
    VolumeAlreadyExists,

    /// <summary>The volume was created by a future application version and cannot be opened.</summary>
    VolumeIncompatibleVersion,

    // ── Auth ──────────────────────────────────────────────────────────────────

    /// <summary>The supplied unlock password did not produce the correct KEK.</summary>
    InvalidPassword,

    /// <summary>The supplied recovery phrase is malformed, has wrong checksum, or contains unknown words.</summary>
    InvalidMnemonic,

    /// <summary>Key derivation (Argon2id or HKDF) failed due to a cryptographic library error.</summary>
    KeyDerivationFailed,

    /// <summary>The OAuth access token has expired and cannot be used for the requested operation.</summary>
    TokenExpired,

    /// <summary>Refreshing the OAuth access token failed.</summary>
    TokenRefreshFailed,

    /// <summary>The OAuth token has been revoked by the provider or the user.</summary>
    TokenRevoked,

    // ── Providers ─────────────────────────────────────────────────────────────

    /// <summary>The cloud provider could not be reached (network, DNS, or endpoint down).</summary>
    ProviderUnreachable,

    /// <summary>Authentication to the cloud provider failed.</summary>
    ProviderAuthFailed,

    /// <summary>The cloud provider account has insufficient storage quota.</summary>
    ProviderQuotaExceeded,

    /// <summary>The cloud provider is rate-limiting requests; the operation was not attempted.</summary>
    ProviderRateLimited,

    /// <summary>The provider's API has changed in an incompatible way; the adapter needs updating.</summary>
    ProviderApiChanged,

    /// <summary>An upload to a tail failed after exhausting retries.</summary>
    UploadFailed,

    /// <summary>The resumable-upload session on the provider side has expired; a fresh upload is required.</summary>
    UploadSessionExpired,

    /// <summary>Downloading a blob from a tail failed.</summary>
    DownloadFailed,

    /// <summary>The requested blob does not exist on the provider.</summary>
    BlobNotFound,

    /// <summary>A downloaded blob failed integrity verification (wrong size or checksum mismatch).</summary>
    BlobCorrupt,

    // ── Pipeline ──────────────────────────────────────────────────────────────

    /// <summary>The compression stage failed.</summary>
    CompressionFailed,

    /// <summary>AES-256-GCM encryption failed.</summary>
    EncryptionFailed,

    /// <summary>AES-256-GCM decryption failed (wrong key, wrong nonce, or tampered ciphertext).</summary>
    DecryptionFailed,

    /// <summary>Post-download integrity verification failed; the data does not match the expected digest.</summary>
    IntegrityCheckFailed,

    /// <summary>A stored checksum does not match the computed checksum of the data.</summary>
    ChecksumMismatch,

    // ── Metadata ──────────────────────────────────────────────────────────────

    /// <summary>The brain database file is corrupt and cannot be opened or queried.</summary>
    DatabaseCorrupt,

    /// <summary>The brain database is locked by another process or connection.</summary>
    DatabaseLocked,

    /// <summary>A read from the brain database failed.</summary>
    DatabaseReadFailed,

    /// <summary>A write to the brain database failed.</summary>
    DatabaseWriteFailed,

    /// <summary>A schema migration step failed.</summary>
    DatabaseMigrationFailed,

    /// <summary>WAL recovery could not restore the crash-consistency invariant.</summary>
    WalRecoveryFailed,

    /// <summary>The brain schema version is not compatible with this application version.</summary>
    SchemaVersionMismatch,

    // ── USB / IO ──────────────────────────────────────────────────────────────

    /// <summary>The skink USB drive was removed while an operation was in progress.</summary>
    UsbRemoved,

    /// <summary>No suitable USB drive was found.</summary>
    UsbNotFound,

    /// <summary>The skink USB drive is mounted read-only.</summary>
    UsbReadOnly,

    /// <summary>The skink USB drive has insufficient free space.</summary>
    UsbFull,

    /// <summary>Writing to the staging area on the skink failed.</summary>
    StagingFailed,

    /// <summary>Neither the skink nor any tail has sufficient space for the operation.</summary>
    InsufficientDiskSpace,

    /// <summary>Another FlashSkink process holds the single-instance lock on this volume.</summary>
    SingleInstanceLockHeld,

    // ── File operations ───────────────────────────────────────────────────────

    /// <summary>The source file was modified while it was being read into the pipeline.</summary>
    FileChangedDuringRead,

    /// <summary>The file exceeds the maximum supported size.</summary>
    FileTooLong,

    /// <summary>A rename or move would create a path conflict in the metadata tree.</summary>
    PathConflict,

    /// <summary>A move operation would create a cyclic parent–child relationship.</summary>
    CyclicMoveDetected,

    /// <summary>The operation requires explicit user confirmation that was not provided.</summary>
    ConfirmationRequired,

    /// <summary>The file type is not supported by the pipeline.</summary>
    UnsupportedFileType,

    /// <summary>The requested file does not exist in the metadata tree.</summary>
    FileNotFound,

    // ── Recovery ──────────────────────────────────────────────────────────────

    /// <summary>Volume recovery from a tail or mnemonic failed.</summary>
    RecoveryFailed,

    /// <summary>No backup tail or surviving copy was found to recover from.</summary>
    NoBackupFound,

    /// <summary>Decrypting a backup during recovery failed.</summary>
    BackupDecryptFailed,

    // ── Healing ───────────────────────────────────────────────────────────────

    /// <summary>The self-healing service could not repair the detected divergence.</summary>
    HealingFailed,

    /// <summary>Self-healing requires at least one surviving tail, and none are available.</summary>
    NoSurvivorsAvailable,
}

namespace FlashSkink.Core.Metadata;

/// <summary>
/// One row from the <c>WAL</c> brain table. Represents the state of a multi-step operation
/// in the crash-recovery state machine. Rows in phases other than <c>COMMITTED</c> or
/// <c>FAILED</c> are processed by the WAL recovery sweep at startup (Phase 5).
/// </summary>
public sealed record WalRow(
    string WalId,
    /// <summary>WRITE | DELETE | CASCADE_DELETE | TAIL_DELETE | PURGE</summary>
    string Operation,
    /// <summary>PREPARE | COMMITTED | FAILED</summary>
    string Phase,
    DateTime StartedUtc,
    DateTime UpdatedUtc,
    /// <summary>JSON payload with operation-specific context.</summary>
    string Payload);

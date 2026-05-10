namespace FlashSkink.Core.Storage;

/// <summary>
/// Cross-platform fsync helpers shared by <see cref="AtomicBlobWriter"/> and
/// <see cref="FlashSkink.Core.Providers.FileSystemProvider"/>. Blueprint §13.4, Principle 29.
/// </summary>
internal static class AtomicWriteHelper
{
    /// <summary>
    /// Fsyncs the directory at <paramref name="directoryPath"/> to flush its inode metadata to
    /// disk. No-op on Windows (NTFS metadata journaling makes directory-entry durability a given
    /// once <see cref="File.Move"/> returns). On Linux/macOS opens a directory handle and calls
    /// <see cref="RandomAccess.FlushToDisk"/>. Best-effort: exceptions are swallowed because
    /// modern journaled filesystems (ext4 data=ordered, APFS) make renames durable after the
    /// preceding file fsync without an explicit directory fsync.
    /// </summary>
    internal static void FsyncDirectory(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var handle = File.OpenHandle(
                directoryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.None);
            RandomAccess.FlushToDisk(handle);
        }
        catch (UnauthorizedAccessException) { /* best-effort: directory fsync not supported in this environment */ }
        catch (IOException) { /* best-effort: proceed without directory fsync */ }
    }

    /// <summary>
    /// Writes <paramref name="data"/> to <paramref name="filePath"/>, flushes, and fsyncs the
    /// file handle. Does not create the containing directory — the caller is responsible.
    /// </summary>
    internal static async Task WriteAndFsyncAsync(string filePath, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await using var fs = new FileStream(
            filePath,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.None,
                PreallocationSize = data.Length,
            });
        await fs.WriteAsync(data, ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
        RandomAccess.FlushToDisk(fs.SafeFileHandle);
    }
}

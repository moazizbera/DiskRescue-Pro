namespace DiskRescue.IO;

/// <summary>
/// Owns uniquely named temporary files for one deleted-file export. The exclusive
/// lock remains open for the lifetime of the operation. If the process terminates,
/// a later operation can acquire the abandoned lock and remove only that operation's
/// exact temporary filenames.
/// </summary>
public sealed class DeletedRecoveryTemporaryFileLease : IDisposable
{
    private const string LockPrefix = ".diskrescue-";
    private const string LockSuffix = ".lock";
    private const string MarkerPrefix = "DiskRescuePro.DeletedExport.v1\n";
    private readonly string _lockPath;
    private FileStream? _lockHandle;

    private DeletedRecoveryTemporaryFileLease(string directory, Guid operationId, FileStream lockHandle)
    {
        var token = operationId.ToString("N");
        _lockPath = Path.Combine(directory, LockPrefix + token + LockSuffix);
        OutputPath = Path.Combine(directory, LockPrefix + token + ".output.partial");
        ManifestPath = Path.Combine(directory, LockPrefix + token + ".manifest.partial");
        _lockHandle = lockHandle;
    }

    public string OutputPath { get; }
    public string ManifestPath { get; }

    public static DeletedRecoveryTemporaryFileLease Create(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Recovery output directory must already exist.");

        CleanupStale(directory);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var operationId = Guid.NewGuid();
            var token = operationId.ToString("N");
            var lockPath = Path.Combine(directory, LockPrefix + token + LockSuffix);
            try
            {
                var handle = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    var marker = System.Text.Encoding.UTF8.GetBytes(MarkerPrefix + token);
                    handle.Write(marker);
                    handle.Flush(true);
                    return new DeletedRecoveryTemporaryFileLease(directory, operationId, handle);
                }
                catch
                {
                    handle.Dispose();
                    try { File.Delete(lockPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    throw;
                }
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                // A GUID collision is extraordinarily unlikely; retry without reusing a path.
            }
        }

        throw new IOException("Could not reserve a unique recovery temporary-file operation.");
    }

    /// <summary>
    /// Removes temporary files only when their matching lock can be opened exclusively
    /// and contains the exact versioned operation marker. Active operations are skipped.
    /// </summary>
    public static int CleanupStale(string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var directory = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Recovery output directory must already exist.");

        var removed = 0;
        foreach (var lockPath in Directory.EnumerateFiles(directory, LockPrefix + "*" + LockSuffix, SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(lockPath);
            var token = fileName.Substring(LockPrefix.Length, fileName.Length - LockPrefix.Length - LockSuffix.Length);
            if (!Guid.TryParseExact(token, "N", out _))
                continue;

            FileStream? handle = null;
            var artifactsRemoved = false;
            try
            {
                handle = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using (var reader = new StreamReader(handle, System.Text.Encoding.UTF8, false, 1024, leaveOpen: true))
                {
                    var marker = reader.ReadToEnd();
                    if (!string.Equals(marker, MarkerPrefix + token, StringComparison.Ordinal))
                        continue;
                }

                var outputRemoved = TryDelete(Path.Combine(directory, LockPrefix + token + ".output.partial"));
                var manifestRemoved = TryDelete(Path.Combine(directory, LockPrefix + token + ".manifest.partial"));
                artifactsRemoved = outputRemoved && manifestRemoved;
            }
            catch (IOException)
            {
                // The lock is active, inaccessible, or malformed. Never guess.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve artifacts when cleanup permissions are uncertain.
            }
            finally
            {
                handle?.Dispose();
            }

            if (artifactsRemoved && TryDelete(lockPath))
                removed++;
        }

        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return !File.Exists(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_lockHandle is null)
            return;

        var outputRemoved = TryDelete(OutputPath);
        var manifestRemoved = TryDelete(ManifestPath);
        _lockHandle.Dispose();
        _lockHandle = null;

        // Keep the lock if cleanup failed so a later invocation can retry safely.
        if (outputRemoved && manifestRemoved)
            TryDelete(_lockPath);
    }
}

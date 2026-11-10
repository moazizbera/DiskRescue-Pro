namespace DiskRescue.IO;

/// <summary>
/// Stable identity of the directory object pinned for one physical acquisition.
/// VolumeIdentity is a normalized volume identifier; FileIdHigh/FileIdLow are the
/// filesystem-provided 128-bit file identifier. These values are scoped to the
/// filesystem and are not globally unique.
/// </summary>
internal readonly record struct PhysicalDestinationDirectoryIdentity(
    string VolumeIdentity,
    ulong FileIdHigh,
    ulong FileIdLow);

/// <summary>
/// Stable identity of a file created through a pinned destination session.
/// The volume identity must match the owning directory session before source reads.
/// </summary>
internal readonly record struct PhysicalDestinationFileIdentity(
    string VolumeIdentity,
    ulong FileIdHigh,
    ulong FileIdLow);

/// <summary>
/// Handle-bound destination operations for one physical acquisition. Implementations
/// must accept only leaf names, create files relative to the pinned directory handle,
/// and keep verification and publication bound to the same open file handle.
/// No arbitrary destination path is accepted after the session is opened.
/// </summary>
internal interface IPhysicalAcquisitionDestinationSession : IAsyncDisposable
{
    PhysicalDestinationDirectoryIdentity DirectoryIdentity { get; }

    /// <summary>
    /// Creates a new read/write stream relative to the pinned directory. The returned
    /// stream must remain owned by this session and support reading, writing, seeking,
    /// and durable flush. Existing names must never be opened or overwritten.
    /// </summary>
    ValueTask<Stream> CreateNewFileAsync(
        string leafName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the identity of a stream created by this session. Implementations must
    /// reject foreign streams and must not resolve the file again by pathname.
    /// </summary>
    ValueTask<PhysicalDestinationFileIdentity> GetFileIdentityAsync(
        Stream file,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes file data and required metadata through the already-open file handle.
    /// </summary>
    ValueTask FlushToDiskAsync(
        Stream file,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes an already-open session-owned file by handle-relative, no-replace
    /// rename. The stream remains valid until disposed; an existing target is preserved.
    /// </summary>
    ValueTask PublishNoReplaceAsync(
        Stream file,
        string finalLeafName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an unpublished session-owned temporary file by handle, never by an
    /// independently re-resolved path.
    /// </summary>
    ValueTask DeleteUnpublishedAsync(
        Stream file,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates names passed to handle-relative destination operations. This is a
/// defense-in-depth boundary, not a substitute for native relative-open semantics.
/// </summary>
internal static class PhysicalDestinationLeafName
{
    public static string Validate(string leafName, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leafName, parameterName);

        if (leafName.Length > 255 ||
            leafName is "." or ".." ||
            leafName.EndsWith(' ') ||
            leafName.EndsWith('.') ||
            leafName.Any(character =>
                character is '/' or '\\' or ':' or '<' or '>' or '"' or '|' or '?' or '*' or '\0' ||
                char.IsControl(character)))
        {
            throw new ArgumentException(
                "Destination entry must be a single valid filesystem leaf name.",
                parameterName);
        }

        var deviceStem = leafName.Split('.')[0];
        if (deviceStem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            deviceStem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            deviceStem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            deviceStem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (deviceStem.Length == 4 &&
             (deviceStem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
              deviceStem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             deviceStem[3] is >= '1' and <= '9'))
        {
            throw new ArgumentException(
                "Destination entry uses a reserved Windows device name.",
                parameterName);
        }

        return leafName;
    }
}

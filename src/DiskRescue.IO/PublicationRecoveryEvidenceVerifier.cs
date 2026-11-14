namespace DiskRescue.IO;

internal sealed record PublicationRecoveryFileEvidence(long Length, string Sha256);

internal sealed record PublicationRecoverySnapshot(
    PhysicalAcquisitionPublicationCheckpoint Checkpoint,
    PublicationRecoveryFileEvidence? ImageFinal,
    PublicationRecoveryFileEvidence? ManifestFinal,
    PublicationRecoveryFileEvidence? ImageTemporary,
    PublicationRecoveryFileEvidence? ManifestTemporary);

internal sealed record PublicationRecoveryExpectation(
    long ImageLength,
    string ImageSha256,
    long ManifestLength,
    string ManifestSha256);

internal sealed record PublicationRecoveryVerificationResult(
    bool Passed,
    IReadOnlyList<string> Findings);

/// <summary>
/// Pure verifier for a read-only snapshot collected after recovery from a disposable
/// test image. It performs no filesystem, VM, volume, or device operations.
/// </summary>
internal static class PublicationRecoveryEvidenceVerifier
{
    public static PublicationRecoveryVerificationResult Verify(
        PublicationRecoveryExpectation expected,
        PublicationRecoverySnapshot observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);

        var findings = new List<string>();
        if (expected.ImageLength < 0 || expected.ManifestLength < 0)
            findings.Add("Expected file lengths cannot be negative.");
        if (!IsSha256(expected.ImageSha256) || !IsSha256(expected.ManifestSha256))
            findings.Add("Expected SHA-256 values must contain exactly 64 hexadecimal characters.");
        if (findings.Count != 0)
            return new(false, findings.AsReadOnly());

        CheckEntry("final image", observed.ImageFinal, expected.ImageLength, expected.ImageSha256, findings);
        CheckEntry("temporary image", observed.ImageTemporary, expected.ImageLength, expected.ImageSha256, findings);
        CheckEntry("final manifest", observed.ManifestFinal, expected.ManifestLength, expected.ManifestSha256, findings);
        CheckEntry("temporary manifest", observed.ManifestTemporary, expected.ManifestLength, expected.ManifestSha256, findings);

        switch (observed.Checkpoint)
        {
            case PhysicalAcquisitionPublicationCheckpoint.ImageDataFlushedBeforeRename:
                RequireAbsent("final image", observed.ImageFinal, findings);
                RequireAbsent("final manifest", observed.ManifestFinal, findings);
                RequireAbsent("temporary manifest", observed.ManifestTemporary, findings);
                break;

            case PhysicalAcquisitionPublicationCheckpoint.ImageRenamedBeforeNamespaceFlush:
                RequireAbsent("final manifest", observed.ManifestFinal, findings);
                RequireAbsent("temporary manifest", observed.ManifestTemporary, findings);
                if (observed.ImageFinal is not null && observed.ImageTemporary is not null)
                    findings.Add("Both final and temporary image entries are present; publication state is ambiguous.");
                break;

            case PhysicalAcquisitionPublicationCheckpoint.ImageNamespaceFlushed:
                RequirePresent("final image", observed.ImageFinal, findings);
                RequireAbsent("temporary image", observed.ImageTemporary, findings);
                RequireAbsent("final manifest", observed.ManifestFinal, findings);
                RequireAbsent("temporary manifest", observed.ManifestTemporary, findings);
                break;

            case PhysicalAcquisitionPublicationCheckpoint.ManifestRenamedBeforeNamespaceFlush:
                RequirePresent("final image", observed.ImageFinal, findings);
                RequireAbsent("temporary image", observed.ImageTemporary, findings);
                RequireAtMostOne("manifest", observed.ManifestFinal, observed.ManifestTemporary, findings);
                break;

            case PhysicalAcquisitionPublicationCheckpoint.ManifestNamespaceFlushed:
                RequirePresent("final image", observed.ImageFinal, findings);
                RequirePresent("final manifest", observed.ManifestFinal, findings);
                RequireAbsent("temporary image", observed.ImageTemporary, findings);
                RequireAbsent("temporary manifest", observed.ManifestTemporary, findings);
                break;

            default:
                findings.Add("The supplied checkpoint is not a supported post-publication recovery phase.");
                break;
        }

        return new(findings.Count == 0, findings.AsReadOnly());
    }

    private static void CheckEntry(
        string label,
        PublicationRecoveryFileEvidence? entry,
        long expectedLength,
        string expectedHash,
        List<string> findings)
    {
        if (entry is null) return;
        if (entry.Length != expectedLength)
            findings.Add($"The {label} length does not match the expected value.");
        if (!IsSha256(entry.Sha256) ||
            !StringComparer.OrdinalIgnoreCase.Equals(entry.Sha256, expectedHash))
            findings.Add($"The {label} SHA-256 does not match the expected value.");
    }

    private static void RequirePresent(string label, PublicationRecoveryFileEvidence? entry, List<string> findings)
    {
        if (entry is null) findings.Add($"The {label} is missing for this checkpoint.");
    }

    private static void RequireAbsent(string label, PublicationRecoveryFileEvidence? entry, List<string> findings)
    {
        if (entry is not null) findings.Add($"The {label} must not be present for this checkpoint.");
    }

    private static void RequireAtMostOne(
        string label,
        PublicationRecoveryFileEvidence? final,
        PublicationRecoveryFileEvidence? temporary,
        List<string> findings)
    {
        if (final is not null && temporary is not null)
            findings.Add($"Both final and temporary {label} entries are present; publication state is ambiguous.");
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

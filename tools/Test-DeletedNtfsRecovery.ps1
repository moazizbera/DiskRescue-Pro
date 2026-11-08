[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ("DiskRescue-DeletedRecovery-Source-" + [Guid]::NewGuid().ToString("N"))),
    [string]$DestinationDirectory = (Join-Path $env:SystemDrive ("DiskRescue-DeletedRecovery-Destination-" + [Guid]::NewGuid().ToString("N")))
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repoRoot "src/DiskRescue.Cli/DiskRescue.Cli.csproj"
$fixtureGenerator = Join-Path $PSScriptRoot "New-DeletedNtfsDataImage.ps1"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK is required."
}
if (-not (Test-Path $cliProject)) {
    throw "DiskRescue CLI project was not found: $cliProject"
}

$directory = [IO.Path]::GetFullPath($OutputDirectory)
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
New-Item -ItemType Directory -Path $destination -Force | Out-Null
if ([string]::Equals([IO.Path]::GetPathRoot($directory), [IO.Path]::GetPathRoot($destination), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Synthetic source images and recovery outputs must be placed on different drive roots for the destination-separation test."
}
$freeImage = Join-Path $directory "deleted-ntfs-free.img"
$allocatedImage = Join-Path $directory "deleted-ntfs-allocated.img"
$fragmentedImage = Join-Path $directory "deleted-ntfs-fragmented.img"
$fragmentedAllocatedImage = Join-Path $directory "deleted-ntfs-fragmented-allocated.img"
$fragmentedOutput = Join-Path $destination "Deleted-fragmented-recovered.bin"
$output = Join-Path $destination "Deleted-recovered.bin"
$manifestPath = "${output}.recovery.json"

foreach ($path in @($freeImage, $allocatedImage, $fragmentedImage, $fragmentedAllocatedImage, $fragmentedOutput, "${fragmentedOutput}.recovery.json", $output, $manifestPath)) {
    if (Test-Path $path) { throw "Refusing to overwrite an existing test artifact: $path" }
}

function Invoke-DiskRescueCli {
    param([string[]]$Arguments)
    $lines = @(& dotnet run --project $cliProject --configuration Release --no-build -- @Arguments 2>&1)
    $code = $LASTEXITCODE
    return [pscustomobject]@{
        ExitCode = $code
        Output = ($lines | ForEach-Object { "$_" }) -join [Environment]::NewLine
    }
}

try {
    Write-Host "[1/6] Building the CLI..."
    & dotnet build $cliProject --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "DiskRescue CLI build failed." }

    Write-Host "[2/6] Creating disposable synthetic deleted-NTFS images..."
    & $fixtureGenerator -OutputPath $freeImage
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $freeImage)) { throw "Free-cluster fixture generation failed." }
    & $fixtureGenerator -OutputPath $allocatedImage -MarkDataClustersAllocated
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $allocatedImage)) { throw "Allocated-cluster fixture generation failed." }
    & $fixtureGenerator -OutputPath $fragmentedImage -Fragmented
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $fragmentedImage)) { throw "Fragmented multi-extent fixture generation failed." }
    & $fixtureGenerator -OutputPath $fragmentedAllocatedImage -Fragmented -MarkDataClustersAllocated
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $fragmentedAllocatedImage)) { throw "Fragmented allocated-cluster fixture generation failed." }

    $sourceHashBefore = (Get-FileHash -LiteralPath $freeImage -Algorithm SHA256).Hash
    $fragmentedSourceHashBefore = (Get-FileHash -LiteralPath $fragmentedImage -Algorithm SHA256).Hash
    Write-Host "[3/6] Running bounded deleted-data export and validating the manifest..."
    $success = Invoke-DiskRescueCli -Arguments @(
        "extract-deleted-ntfs-file", $freeImage,
        "--offset", "512", "--length", "65024", "--record", "1",
        "--output", $output, "--max-bytes", "1024"
    )
    if ($success.ExitCode -ne 0) { throw "Deleted-data CLI failed: $($success.Output)" }
    if (-not (Test-Path $output) -or -not (Test-Path $manifestPath)) {
        throw "The CLI must publish both recovered bytes and a recovery manifest."
    }

    $expected = New-Object byte[] 512
    for ($i = 0; $i -lt $expected.Length; $i++) { $expected[$i] = [byte]($i % 251) }
    $actual = [IO.File]::ReadAllBytes($output)
    if ([Convert]::ToHexString($actual) -ne [Convert]::ToHexString($expected)) {
        throw "Recovered bytes do not match the deterministic synthetic payload."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $outputHash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
    if ($manifest.manifestVersion -ne 1 -or
        $manifest.operation -ne "bounded-deleted-ntfs-data-stream-export" -or
        $manifest.source.volumeOffsetBytes -ne 512 -or
        $manifest.source.volumeLengthBytes -ne 65024 -or
        $manifest.deletedFile.mftRecordNumber -ne 1 -or
        $manifest.deletedFile.metadataAssessment -ne "Verified" -or
        $manifest.deletedFile.clusterAllocation -ne "Free" -or
        $manifest.export.bytesWritten -ne 512 -or
        $manifest.dataStream.lengthBytes -ne 512 -or
        $manifest.dataStream.physicalRunCount -ne 1 -or
        $manifest.dataStream.physicalRuns[0].startingLcn -ne 30 -or
        $manifest.export.sha256 -ne $outputHash -or
        $manifest.export.contentIntegrityVerified -ne $false -or
        $manifest.safety.sourceOpenedReadOnly -ne $true -or
        $manifest.safety.sourceWriteOperationsAvailable -ne $false -or
        $manifest.safety.physicalDiskRecoveryValidated -ne $false) {
        throw "Recovery manifest is missing or misstates required provenance and safety fields."
    }
    if (($success.Output -notmatch "Content integrity verified: false") -or
        ($success.Output -notmatch "Recovery manifest:")) {
        throw "CLI output did not disclose the manifest and unverified content-integrity status."
    }

    Write-Host "[3a] Exporting a deleted file whose VCN extents map to fragmented physical clusters..."
    $fragmented = Invoke-DiskRescueCli -Arguments @(
        "extract-deleted-ntfs-file", $fragmentedImage,
        "--offset", "512", "--length", "65024", "--record", "1",
        "--output", $fragmentedOutput, "--max-bytes", "1024"
    )
    if ($fragmented.ExitCode -ne 0) { throw "Fragmented multi-extent export failed: $($fragmented.Output)" }
    if (-not (Test-Path $fragmentedOutput) -or -not (Test-Path "${fragmentedOutput}.recovery.json")) {
        throw "Fragmented export did not publish both output and recovery manifest."
    }
    $fragmentedExpected = New-Object byte[] 1024
    for ($i = 0; $i -lt $fragmentedExpected.Length; $i++) { $fragmentedExpected[$i] = [byte]($i % 251) }
    $fragmentedActual = [IO.File]::ReadAllBytes($fragmentedOutput)
    if ([Convert]::ToHexString($fragmentedActual) -ne [Convert]::ToHexString($fragmentedExpected)) {
        throw "Fragmented multi-extent output does not match the deterministic 1024-byte payload."
    }
    $fragmentedManifest = Get-Content -LiteralPath "${fragmentedOutput}.recovery.json" -Raw | ConvertFrom-Json
    $fragmentedOutputHash = (Get-FileHash -LiteralPath $fragmentedOutput -Algorithm SHA256).Hash
    if ($fragmentedManifest.export.bytesWritten -ne 1024 -or
        $fragmentedManifest.dataStream.lengthBytes -ne 1024 -or
        $fragmentedManifest.dataStream.initializedSizeBytes -ne 1024 -or
        $fragmentedManifest.dataStream.physicalRunCount -ne 2 -or
        $fragmentedManifest.dataStream.physicalRuns[0].startingLcn -ne 30 -or
        $fragmentedManifest.dataStream.physicalRuns[1].startingLcn -ne 34 -or
        $fragmentedManifest.export.sha256 -ne $fragmentedOutputHash -or
        $fragmentedManifest.export.contentIntegrityVerified -ne $false) {
        throw "Fragmented recovery manifest does not accurately describe the bounded export."
    }
    if ((Get-FileHash -LiteralPath $fragmentedImage -Algorithm SHA256).Hash -ne $fragmentedSourceHashBefore) {
        throw "Fragmented source image changed during successful multi-extent export."
    }

    Write-Host "[4/6] Verifying source immutability and refusal of allocated clusters..."
    $sourceHashAfter = (Get-FileHash -LiteralPath $freeImage -Algorithm SHA256).Hash
    if ($sourceHashBefore -ne $sourceHashAfter) { throw "Source image changed during deleted-data export." }

    $allocatedOutput = Join-Path $destination "Allocated-must-not-export.bin"
    $allocated = Invoke-DiskRescueCli -Arguments @(
        "extract-deleted-ntfs-file", $allocatedImage,
        "--offset", "512", "--length", "65024", "--record", "1",
        "--output", $allocatedOutput, "--max-bytes", "1024"
    )
    if ($allocated.ExitCode -eq 0 -or (Test-Path $allocatedOutput) -or (Test-Path "${allocatedOutput}.recovery.json")) {
        throw "The CLI failed to refuse currently allocated clusters."
    }

    $fragmentedAllocatedOutput = Join-Path $destination "Fragmented-allocated-must-not-export.bin"
    $fragmentedAllocated = Invoke-DiskRescueCli -Arguments @(
        "extract-deleted-ntfs-file", $fragmentedAllocatedImage,
        "--offset", "512", "--length", "65024", "--record", "1",
        "--output", $fragmentedAllocatedOutput, "--max-bytes", "1024"
    )
    if ($fragmentedAllocated.ExitCode -eq 0 -or
        (Test-Path $fragmentedAllocatedOutput) -or
        (Test-Path "${fragmentedAllocatedOutput}.recovery.json")) {
        throw "The CLI did not refuse an allocated cluster located only in the continuation extent."
    }

    Write-Host "[5/6] Verifying byte ceiling, no-overwrite, and source-path protection..."
    $limitedOutput = Join-Path $destination "Over-limit.bin"
    $limited = Invoke-DiskRescueCli -Arguments @(
        "extract-deleted-ntfs-file", $freeImage,
        "--offset", "512", "--length", "65024", "--record", "1",
        "--output", $limitedOutput, "--max-bytes", "128"
    )
    if ($limited.ExitCode -eq 0 -or (Test-Path $limitedOutput) -or (Test-Path "${limitedOutput}.recovery.json")) {
        throw "The CLI failed to enforce the caller's maximum byte limit."
    }

    $existingBytesBefore = [IO.File]::ReadAllBytes($output)
    $existing = Invoke-DiskRescueCli -Arguments @(
        "extract-deleted-ntfs-file", $freeImage,
        "--offset", "512", "--length", "65024", "--record", "1",
        "--output", $output, "--max-bytes", "1024"
    )
    if ($existing.ExitCode -eq 0) { throw "The CLI unexpectedly overwrote an existing output." }
    if ([Convert]::ToHexString([IO.File]::ReadAllBytes($output)) -ne [Convert]::ToHexString($existingBytesBefore)) {
        throw "Existing recovery output changed after overwrite refusal."
    }

    $sourceAsOutput = Invoke-DiskRescueCli -Arguments @(
        "extract-deleted-ntfs-file", $freeImage,
        "--offset", "512", "--length", "65024", "--record", "1",
        "--output", $freeImage, "--max-bytes", "1024"
    )
    if ($sourceAsOutput.ExitCode -eq 0) { throw "The CLI unexpectedly accepted the source image as output." }
    if ((Get-FileHash -LiteralPath $freeImage -Algorithm SHA256).Hash -ne $sourceHashBefore) {
        throw "Source image changed after source-path overwrite refusal."
    }

    $manifestCollisionOutput = Join-Path $destination "Manifest-collision.bin"
    [IO.File]::WriteAllText("${manifestCollisionOutput}.recovery.json", "preserve-existing-manifest")
    $manifestCollision = Invoke-DiskRescueCli -Arguments @(
        "extract-deleted-ntfs-file", $freeImage,
        "--offset", "512", "--length", "65024", "--record", "1",
        "--output", $manifestCollisionOutput, "--max-bytes", "1024"
    )
    if ($manifestCollision.ExitCode -eq 0 -or (Test-Path $manifestCollisionOutput)) {
        throw "The CLI did not safely refuse an existing recovery manifest."
    }
    if ((Get-Content -LiteralPath "${manifestCollisionOutput}.recovery.json" -Raw) -ne "preserve-existing-manifest") {
        throw "The CLI modified an existing recovery manifest."
    }

    Write-Host "[6/6] Checking temporary-file cleanup and final hashes..."
    $partials = @(Get-ChildItem -LiteralPath $destination -Force -File | Where-Object { $_.Name.EndsWith(".partial", [StringComparison]::OrdinalIgnoreCase) })
    if ($partials.Count -ne 0) { throw "Temporary partial output files remain after the test run." }

    # Expected negative CLI cases leave a non-zero native exit code; the harness itself passed.
    $global:LASTEXITCODE = 0
    Write-Host ""
    Write-Host "PASS: deleted NTFS CLI export, recovery manifest, source immutability, allocated-cluster refusal, byte ceiling, overwrite protection, and temporary cleanup."
    Write-Host "Synthetic source SHA-256: $sourceHashBefore"
    Write-Host "Recovered output SHA-256: $outputHash"
    Write-Host "Fragmented output SHA-256: $fragmentedOutputHash (two extents, LCN 30 and 34)"
    Write-Host "Recovery manifest: $manifestPath"
    Write-Host "Content integrity: explicitly unverified; no physical disk was accessed."
}
catch {
    Write-Error $_
    exit 1
}

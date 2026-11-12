# Windows-only tests for handle-relative preflight evidence creation.
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not [OperatingSystem]::IsWindows()) {
    throw "Pinned evidence writer tests require Windows."
}

Add-Type -Path (Join-Path $PSScriptRoot "PinnedEvidenceFile.cs") -ErrorAction Stop
$root = Join-Path ([IO.Path]::GetTempPath()) ("DiskRescue-PinnedEvidence-" + [guid]::NewGuid().ToString("N"))
$directory = Join-Path $root "evidence"
[IO.Directory]::CreateDirectory($directory) | Out-Null

try {
    $leaf = "preflight-evidence.json"
    $expected = [Text.UTF8Encoding]::new($false).GetBytes('{"schema":"test"}' + [Environment]::NewLine)

    $stream = [DiskRescue.Preflight.PinnedEvidenceFile]::CreateNew($directory, $leaf)
    try {
        $stream.Write($expected, 0, $expected.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }

    $target = Join-Path $directory $leaf
    $actual = [IO.File]::ReadAllBytes($target)
    if ([Convert]::ToHexString($actual) -ne [Convert]::ToHexString($expected)) {
        throw "Pinned evidence writer did not preserve the expected bytes."
    }

    $collisionRejected = $false
    try {
        $collision = [DiskRescue.Preflight.PinnedEvidenceFile]::CreateNew($directory, $leaf)
        $collision.Dispose()
    }
    catch { $collisionRejected = $true }
    if (-not $collisionRejected) { throw "Pinned evidence writer unexpectedly replaced an existing target." }

    $afterCollision = [IO.File]::ReadAllBytes($target)
    if ([Convert]::ToHexString($afterCollision) -ne [Convert]::ToHexString($expected)) {
        throw "Existing evidence bytes changed after a competing create attempt."
    }

    $invalidLeafRejected = $false
    try {
        $invalid = [DiskRescue.Preflight.PinnedEvidenceFile]::CreateNew($directory, "..\\escape.json")
        $invalid.Dispose()
    }
    catch { $invalidLeafRejected = $true }
    if (-not $invalidLeafRejected) { throw "Pinned evidence writer accepted a path traversal leaf." }

    Write-Output "Pinned evidence writer: create-new, durable flush, collision preservation, and leaf validation passed."
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) ("DiskRescue-Recovery-Test-" + (Get-Date -Format "yyyyMMdd-HHmmss")))
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$cliProject = Join-Path $repoRoot "src/DiskRescue.Cli/DiskRescue.Cli.csproj"
$fixtureGenerator = Join-Path $PSScriptRoot "New-LostNtfsPartitionImage.ps1"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK is required. Install it before running this test."
}
if (-not (Test-Path $cliProject)) {
    throw "DiskRescue CLI project was not found at $cliProject. Run this script from a repository checkout."
}

$directory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$imagePath = Join-Path $directory "lost-ntfs-partition.img"
$recoveredPath = Join-Path $directory "Report-recovered.txt"

if (Test-Path $imagePath) {
    throw "Refusing to overwrite an existing test image: $imagePath"
}
if (Test-Path $recoveredPath) {
    throw "Refusing to overwrite an existing recovered file: $recoveredPath"
}

function Invoke-DiskRescueJson {
    param([string[]]$CliArguments)

    $output = & dotnet run --project $cliProject --configuration Release -- @CliArguments
    if ($LASTEXITCODE -ne 0) {
        throw ("DiskRescue CLI command failed (exit $LASTEXITCODE): " + ($CliArguments -join " ") + [Environment]::NewLine + ($output -join [Environment]::NewLine))
    }

    try {
        return (($output -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop)
    }
    catch {
        throw "DiskRescue CLI returned invalid JSON for: $($CliArguments -join ' ')"
    }
}

try {
    Write-Host "[1/4] Creating a disposable synthetic lost-NTFS image..."
    & $fixtureGenerator -OutputPath $imagePath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $imagePath)) {
        throw "The synthetic test image could not be created."
    }

    Write-Host "[2/4] Discovering the unlisted NTFS partition (read-only)..."
    $discovery = Invoke-DiskRescueJson -CliArguments @("discover-ntfs", $imagePath, "--max-scan-bytes", "4294967296")
    $candidates = @($discovery.partitionDiscovery.candidates | Where-Object { $_.status -eq "Lost partition candidate" })
    if ($candidates.Count -ne 1 -or $candidates[0].byteOffset -ne 1048576 -or
        -not $candidates[0].backupBootSectorValid -or $candidates[0].confidence -ne 95) {
        throw "Expected one validated lost NTFS candidate at byte offset 1,048,576 with confidence 95."
    }
    if (-not $discovery.safety.sourceOpenedReadOnly -or
        $discovery.safety.sourceWriteOperationsAvailable -or
        $discovery.safety.partitionTableChangesAvailable) {
        throw "The discovery report did not satisfy the read-only safety assertions."
    }

    Write-Host "[3/4] Previewing the candidate and extracting the synthetic resident file..."
    $preview = Invoke-DiskRescueJson -CliArguments @("scan-ntfs", $imagePath, "--offset", "1048576", "--length", "65024", "--max-records", "4")
    if ($preview.scan.entries.Count -ne 1 -or $preview.scan.entries[0].name -ne "Report.txt") {
        throw "The bounded NTFS preview did not return the expected Report.txt entry."
    }

    $extractOutput = & dotnet run --project $cliProject --configuration Release -- extract-ntfs-file $imagePath --offset 1048576 --length 65024 --record 1 --output $recoveredPath --max-bytes 1024 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "File extraction failed (exit $LASTEXITCODE): $($extractOutput -join ' ')"
    }
    if (-not (Test-Path $recoveredPath)) {
        throw "The recovered test file was not created."
    }

    Write-Host "[4/4] Verifying recovered content and SHA-256..."
    $expected = [Text.Encoding]::UTF8.GetBytes("DiskRescue synthetic recovered content")
    $actual = [IO.File]::ReadAllBytes($recoveredPath)
    if ([Convert]::ToHexString($actual) -ne [Convert]::ToHexString($expected)) {
        throw "Recovered content does not match the synthetic expected payload."
    }

    $hash = (Get-FileHash -LiteralPath $recoveredPath -Algorithm SHA256).Hash
    Write-Host ""
    Write-Host "PASS: lost-partition discovery, bounded preview, and file extraction verified."
    Write-Host "Synthetic image: $imagePath"
    Write-Host "Recovered test file: $recoveredPath"
    Write-Host "Recovered SHA-256: $hash"
    Write-Host "Safety: only a generated image file was read; no physical disk was accessed."
}
catch {
    Write-Error $_
    exit 1
}

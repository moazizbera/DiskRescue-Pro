#requires -Version 7.0
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $env:TEMP "DiskRescue-Pro-Vhdx-Smoke")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests/DiskRescue.IO.Tests/DiskRescue.IO.Tests.csproj"

if (-not (Get-Command diskpart.exe -ErrorAction SilentlyContinue)) {
    throw "diskpart.exe is required on Windows."
}
if (-not (Get-Command Get-DiskImage -ErrorAction SilentlyContinue)) {
    throw "The Windows Storage module (Get-DiskImage) is required."
}
if (-not (Test-Path $testProject)) {
    throw "DiskRescue IO test project was not found at $testProject."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$vhdPath = Join-Path $OutputDirectory ("diskrescue-safety-" + [guid]::NewGuid().ToString("N") + ".vhdx")
$diskpartPath = Join-Path $OutputDirectory ("diskpart-" + [guid]::NewGuid().ToString("N") + ".txt")
$attached = $false
$testExitCode = 1

try {
    # The VHDX is deliberately left RAW: no partition table, filesystem, or volume is created.
    @"
create vdisk file="$vhdPath" maximum=128 type=expandable
select vdisk file="$vhdPath"
attach vdisk readonly
exit
"@ | Set-Content -LiteralPath $diskpartPath -Encoding Unicode

    & diskpart.exe /s $diskpartPath
    # Check the actual attachment state even if DiskPart reports a non-zero exit;
    # never delete a VHDX that may have been attached before a partial failure.
    $imageState = Get-DiskImage -ImagePath $vhdPath -ErrorAction SilentlyContinue
    $attached = [bool]($imageState -and $imageState.Attached)
    if ($LASTEXITCODE -ne 0) {
        throw "diskpart failed while creating or attaching the disposable VHDX (exit $LASTEXITCODE)."
    }
    if (-not $attached) {
        throw "Windows did not confirm that the disposable VHDX is attached."
    }

    $diskMatches = @(Get-DiskImage -ImagePath $vhdPath | Get-Disk)
    if ($diskMatches.Count -ne 1) {
        throw "Expected exactly one attached disk for the temporary VHDX; found $($diskMatches.Count)."
    }
    $disk = $diskMatches[0]

    if (-not $disk.IsReadOnly) {
        throw "Windows did not report the temporary VHDX as read-only. Test aborted."
    }
    if ($disk.PartitionStyle -ne "RAW") {
        throw "The disposable VHDX is not RAW. Refusing to continue."
    }

    Write-Host "Disposable VHDX attached read-only as PhysicalDrive$($disk.Number)."
    Write-Host "No partitioning, formatting, filesystem creation, or source-sector reads will be performed."

    $env:DISKRESCUE_TEST_VHDX_DISK_NUMBER = [string]$disk.Number
    dotnet test $testProject --configuration Release --filter "FullyQualifiedName~WindowsVhdxSafetySmokeTests" --logger "console;verbosity=normal"
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
        throw "The Windows VHDX safety smoke test failed (dotnet test exit $testExitCode)."
    }

    Write-Host "PASS: the virtual disk was discovered and production acquisition policy blocked it."
}
finally {
    Remove-Item Env:\DISKRESCUE_TEST_VHDX_DISK_NUMBER -ErrorAction SilentlyContinue

    # Re-query Windows rather than trusting only the earlier DiskPart exit code.
    $imageState = Get-DiskImage -ImagePath $vhdPath -ErrorAction SilentlyContinue
    if ($imageState -and $imageState.Attached) {
        $attached = $true
    }

    if ($attached) {
        @"
select vdisk file="$vhdPath"
detach vdisk
exit
"@ | Set-Content -LiteralPath $diskpartPath -Encoding Unicode
        & diskpart.exe /s $diskpartPath
        $detachExitCode = $LASTEXITCODE
        $imageState = Get-DiskImage -ImagePath $vhdPath -ErrorAction SilentlyContinue
        if ($detachExitCode -ne 0 -or ($imageState -and $imageState.Attached)) {
            Write-Warning "Could not confirm VHDX detach. Preserve $vhdPath and detach it manually before deleting it."
        }
        else {
            $attached = $false
        }
    }

    Remove-Item -LiteralPath $diskpartPath -Force -ErrorAction SilentlyContinue
    if (-not $attached -and (Test-Path $vhdPath)) {
        Remove-Item -LiteralPath $vhdPath -Force
    }
}

if ($testExitCode -ne 0) {
    exit $testExitCode
}

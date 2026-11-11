#requires -Version 7.0
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $env:TEMP "DiskRescue-Pro-Vhdx-Enospc")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot "tests/DiskRescue.IO.Tests/DiskRescue.IO.Tests.csproj"

foreach ($command in @("diskpart.exe", "Get-DiskImage", "Get-Disk", "Initialize-Disk", "New-Partition", "Format-Volume", "Get-Partition", "Get-Volume")) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "$command is required on Windows."
    }
}
if (-not (Test-Path $testProject)) {
    throw "DiskRescue IO test project was not found at $testProject."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sourceVhdPath = Join-Path $OutputDirectory ("diskrescue-enospc-source-" + [guid]::NewGuid().ToString("N") + ".vhdx")
$destinationVhdPath = Join-Path $OutputDirectory ("diskrescue-enospc-destination-" + [guid]::NewGuid().ToString("N") + ".vhdx")
$diskpartPath = Join-Path $OutputDirectory ("diskpart-" + [guid]::NewGuid().ToString("N") + ".txt")
$testExitCode = 1
$cleanupErrors = [System.Collections.Generic.List[string]]::new()

function Invoke-DiskPartScript {
    param([Parameter(Mandatory)][string[]]$Commands)

    $body = $Commands -join "`r`n"
    if ([System.Text.Encoding]::ASCII.GetString([System.Text.Encoding]::ASCII.GetBytes($body)) -cne $body) {
        throw "A temporary VHDX path contains non-ASCII characters that cannot be represented safely in the DiskPart script."
    }
    [System.IO.File]::WriteAllText($diskpartPath, $body + "`r`n", [System.Text.Encoding]::ASCII)
    $output = & diskpart.exe /s $diskpartPath 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "DiskPart failed (exit $exitCode). Review the output above."
    }
}

function Get-ExactlyOneAttachedVhdxDisk {
    param([Parameter(Mandatory)][string]$ImagePath)

    $images = @(Get-DiskImage -ImagePath $ImagePath -ErrorAction Stop)
    if ($images.Count -ne 1 -or -not $images[0].Attached) {
        throw "Windows did not confirm exactly one attached image for $ImagePath."
    }
    $expectedPath = [System.IO.Path]::GetFullPath($ImagePath)
    $actualPath = [System.IO.Path]::GetFullPath([string]$images[0].ImagePath)
    if (-not [string]::Equals($expectedPath, $actualPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Get-DiskImage returned an unexpected backing path. Expected $expectedPath; received $actualPath."
    }

    $disks = @($images[0] | Get-Disk -ErrorAction Stop)
    if ($disks.Count -ne 1) {
        throw "Expected exactly one disk associated with $ImagePath; found $($disks.Count)."
    }
    return $disks[0]
}

function Test-VhdxStillAttached {
    param([Parameter(Mandatory)][string]$ImagePath)

    try {
        $image = Get-DiskImage -ImagePath $ImagePath -ErrorAction Stop
        return [bool]($image -and $image.Attached)
    }
    catch {
        Write-Warning "Could not establish attachment state for $ImagePath : $_"
        return $null
    }
}

function Detach-VhdxAndConfirm {
    param([Parameter(Mandatory)][string]$ImagePath)

    if (-not (Test-Path -LiteralPath $ImagePath)) {
        return $true
    }

    $attachedState = Test-VhdxStillAttached -ImagePath $ImagePath
    if ($null -eq $attachedState) {
        return $false
    }
    if ($attachedState) {
        try {
            Invoke-DiskPartScript -Commands @(
                "select vdisk file=`"$ImagePath`""
                "detach vdisk"
                "exit"
            )
        }
        catch {
            Write-Warning "DiskPart detach failed for $ImagePath : $_"
        }
    }

    $confirmedState = Test-VhdxStillAttached -ImagePath $ImagePath
    if ($null -eq $confirmedState -or $confirmedState) {
        Write-Warning "Could not confirm detach for $ImagePath. Preserve the backing file."
        return $false
    }

    Remove-Item -LiteralPath $ImagePath -Force
    return $true
}

try {
    Invoke-DiskPartScript -Commands @(
        "create vdisk file=`"$sourceVhdPath`" maximum=128 type=expandable"
        "select vdisk file=`"$sourceVhdPath`""
        "attach vdisk readonly"
        "create vdisk file=`"$destinationVhdPath`" maximum=96 type=expandable"
        "select vdisk file=`"$destinationVhdPath`""
        "attach vdisk"
        "exit"
    )

    $sourceDisk = Get-ExactlyOneAttachedVhdxDisk -ImagePath $sourceVhdPath
    $destinationDisk = Get-ExactlyOneAttachedVhdxDisk -ImagePath $destinationVhdPath
    $sourceDiskNumber = [int]$sourceDisk.Number
    $destinationDiskNumber = [int]$destinationDisk.Number

    if ($sourceDiskNumber -eq $destinationDiskNumber) {
        throw "Source and destination VHDX unexpectedly resolve to the same disk number."
    }
    if (-not $sourceDisk.IsReadOnly -or $sourceDisk.PartitionStyle -ne "RAW") {
        throw "The source VHDX is not confirmed read-only and RAW. No test will proceed."
    }
    if ($sourceDisk.BusType -notin @(14, 15)) {
        throw "The source VHDX did not report an expected virtual bus type."
    }
    if ($destinationDisk.IsReadOnly -or $destinationDisk.PartitionStyle -ne "RAW") {
        throw "The destination VHDX must be writable and RAW before initialization."
    }
    if ($destinationDisk.BusType -notin @(14, 15) -or $destinationDisk.IsBoot -or $destinationDisk.IsSystem) {
        throw "The destination disk failed virtual-device / non-boot / non-system checks. Refusing to initialize it."
    }
    if ([long]$sourceDisk.Size -ne 128MB -or [long]$destinationDisk.Size -ne 96MB) {
        throw "The attached VHDX capacities do not match the requested 128-MiB source and 96-MiB destination."
    }

    Write-Host "Verified source backing file -> PhysicalDrive$sourceDiskNumber (128 MiB, RAW, read-only)."
    Write-Host "Verified destination backing file -> PhysicalDrive$destinationDiskNumber (96 MiB, RAW, writable, non-boot, non-system)."

    # Re-correlate the destination backing path immediately before the only
    # initialization/format operations in this harness.
    $destinationBeforeInitialize = Get-ExactlyOneAttachedVhdxDisk -ImagePath $destinationVhdPath
    if ([int]$destinationBeforeInitialize.Number -ne $destinationDiskNumber -or
        $destinationBeforeInitialize.IsReadOnly -or
        $destinationBeforeInitialize.PartitionStyle -ne "RAW" -or
        $destinationBeforeInitialize.IsBoot -or
        $destinationBeforeInitialize.IsSystem) {
        throw "The destination VHDX identity or safety state changed before initialization."
    }

    $usedLetters = @(
        (Get-Volume | Where-Object DriveLetter | ForEach-Object { [string]$_.DriveLetter }) +
        (Get-PSDrive -PSProvider FileSystem | ForEach-Object { [string]$_.Name })
    )
    $driveLetter = $null
    foreach ($code in 90..68) {
        $candidate = [string][char]$code
        if ($candidate -notin $usedLetters) {
            $driveLetter = $candidate
            break
        }
    }
    if (-not $driveLetter) {
        throw "No unused drive letter is available for the disposable destination VHDX."
    }

    Initialize-Disk -Number $destinationDiskNumber -PartitionStyle GPT -PassThru | Out-Null
    $destinationAfterInitialize = Get-ExactlyOneAttachedVhdxDisk -ImagePath $destinationVhdPath
    if ([int]$destinationAfterInitialize.Number -ne $destinationDiskNumber) {
        throw "Destination VHDX disk association changed after initialization."
    }

    $partition = New-Partition -DiskNumber $destinationDiskNumber -UseMaximumSize -DriveLetter $driveLetter
    Format-Volume -Partition $partition -FileSystem NTFS -NewFileSystemLabel "DISKRESCUE-ENOSPC" -Confirm:$false -Force | Out-Null

    $verifiedPartition = Get-Partition -DriveLetter $driveLetter -ErrorAction Stop
    $verifiedVolume = Get-Volume -DriveLetter $driveLetter -ErrorAction Stop
    if ([int]$verifiedPartition.DiskNumber -ne $destinationDiskNumber -or
        $verifiedVolume.FileSystem -ne "NTFS" -or
        [string]$verifiedVolume.DriveLetter -ne $driveLetter) {
        throw "The formatted NTFS volume could not be reconciled to the verified destination VHDX."
    }

    # Reconfirm the source remains read-only and destination remains associated
    # with its exact backing file after the filesystem has been created.
    $sourceAfterFormat = Get-ExactlyOneAttachedVhdxDisk -ImagePath $sourceVhdPath
    $destinationAfterFormat = Get-ExactlyOneAttachedVhdxDisk -ImagePath $destinationVhdPath
    if (-not $sourceAfterFormat.IsReadOnly -or [int]$sourceAfterFormat.Number -ne $sourceDiskNumber) {
        throw "The source VHDX read-only state or disk association changed."
    }
    if ([int]$destinationAfterFormat.Number -ne $destinationDiskNumber) {
        throw "The destination VHDX disk association changed after formatting."
    }

    $destinationRoot = '{0}:\' -f $driveLetter
    $destinationDrive = [System.IO.DriveInfo]::new($destinationRoot)
    if (-not $destinationDrive.IsReady -or $destinationDrive.DriveType -ne [System.IO.DriveType]::Fixed -or $destinationDrive.DriveFormat -ne "NTFS") {
        throw "The native destination adapter requires a ready local fixed NTFS volume."
    }
    if ($destinationDrive.AvailableFreeSpace -ge [long]$sourceDisk.Size) {
        throw "Destination free space ($($destinationDrive.AvailableFreeSpace)) is not below source length ($($sourceDisk.Size)); refusing to call this an ENOSPC test."
    }

    Write-Host "Verified destination root $destinationRoot on PhysicalDrive$destinationDiskNumber."
    Write-Host "Measured free bytes before acquisition: $($destinationDrive.AvailableFreeSpace)."
    Write-Host "Only the newly created destination VHDX was initialized and formatted."

    $env:DISKRESCUE_TEST_VHDX_DISK_NUMBER = [string]$sourceDiskNumber
    $env:DISKRESCUE_TEST_DESTINATION_ROOT = $destinationRoot
    dotnet test $testProject --configuration Release --no-build --filter "FullyQualifiedName~UndersizedNtfsDestinationVhdxProducesRealDiskFullAndCleansPartialImage" --logger "console;verbosity=normal"
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
        throw "The genuine ENOSPC VHDX integration test failed (dotnet test exit $testExitCode)."
    }

    Write-Host "PASS: the native coordinator encountered real destination disk-full, removed its unpublished partial image, published no final image/manifest, and left the read-only source hash unchanged."
}
finally {
    Remove-Item Env:\DISKRESCUE_TEST_VHDX_DISK_NUMBER -ErrorAction SilentlyContinue
    Remove-Item Env:\DISKRESCUE_TEST_DESTINATION_ROOT -ErrorAction SilentlyContinue

    foreach ($imagePath in @($sourceVhdPath, $destinationVhdPath)) {
        if (-not (Detach-VhdxAndConfirm -ImagePath $imagePath)) {
            $cleanupErrors.Add("Could not confirm detach; preserved $imagePath")
        }
    }

    Remove-Item -LiteralPath $diskpartPath -Force -ErrorAction SilentlyContinue
}

if ($cleanupErrors.Count -gt 0) {
    throw ($cleanupErrors -join [Environment]::NewLine)
}
if ($testExitCode -ne 0) {
    exit $testExitCode
}

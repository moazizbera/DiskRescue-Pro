#requires -Version 7.0
#requires -Modules Hyper-V
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$VmId,
    [Parameter(Mandatory)][string]$VmName,
    [Parameter(Mandatory)][string]$BootVhdxPath,
    [Parameter(Mandatory)][string]$DataVhdxPath,
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][ValidateSet("ImageDataFlushedBeforeRename","ImageRenamedBeforeNamespaceFlush","ImageNamespaceFlushed","ManifestRenamedBeforeNamespaceFlush","ManifestNamespaceFlushed")][string]$Phase,
    [Parameter(Mandatory)][long]$FixtureLength,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$FixtureSha256,
    [Parameter(Mandatory)][long]$BaselineLength,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$BaselineSha256,
    [Parameter(Mandatory)][string]$BaselineFileId,
    [Parameter(Mandatory)][string]$EvidencePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot "HyperVCrashPreflight.Validation.psm1") -Force

function Get-CanonicalPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not [System.IO.Path]::IsPathRooted($full) -or $full -notmatch '^[A-Za-z]:[\\/]') {
        throw "Only local drive-letter VHDX paths are accepted: $Path"
    }
    return $full
}

function Assert-NoReparsePointInPath {
    param([Parameter(Mandatory)][string]$Path)
    $current = [System.IO.Path]::GetFullPath($Path)
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A reparse point exists in the VHDX path chain: $current"
        }
        $parent = [System.IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}

if (-not ("DiskRescue.Preflight.NativeFileIdentity" -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace DiskRescue.Preflight {
    [StructLayout(LayoutKind.Sequential)]
    public struct ByHandleFileInformation {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    public static class NativeFileIdentity {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        public static extern SafeFileHandle CreateFile(
            string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(
            SafeFileHandle handle, out ByHandleFileInformation info);
    }
}
'@
}

function Get-ReadOnlyFileIdentity {
    param([Parameter(Mandatory)][string]$Path)
    # Desired access is zero: query metadata only. Share read/write/delete;
    # OPEN_EXISTING and BACKUP_SEMANTICS do not create or mutate the file.
    $handle = [DiskRescue.Preflight.NativeFileIdentity]::CreateFile(
        $Path, 0, 7, [IntPtr]::Zero, 3, 0x02000000, [IntPtr]::Zero)
    if ($handle.IsInvalid) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $handle.Dispose()
        throw [ComponentModel.Win32Exception]::new($code, "Could not open VHDX metadata handle read-only.")
    }
    try {
        $info = [DiskRescue.Preflight.ByHandleFileInformation]::new()
        if (-not [DiskRescue.Preflight.NativeFileIdentity]::GetFileInformationByHandle($handle, [ref]$info)) {
            $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw [ComponentModel.Win32Exception]::new($code, "Could not query VHDX file identity.")
        }
        if ($info.NumberOfLinks -ne 1) {
            throw "VHDX path has $($info.NumberOfLinks) hard links; aliases are not permitted."
        }
        return [pscustomobject]@{
            FileId = ("volume-serial:{0:X8};file-id:{1:X8}{2:X8}" -f $info.VolumeSerialNumber, $info.FileIndexHigh, $info.FileIndexLow)
            LinkCount = [long]$info.NumberOfLinks
        }
    }
    finally { $handle.Dispose() }
}

function Get-VhdxEvidence {
    param([Parameter(Mandatory)][string]$Path)
    $canonical = Get-CanonicalPath $Path
    Assert-NoReparsePointInPath $canonical
    $vhd = Get-VHD -Path $canonical -ErrorAction Stop
    $item = Get-Item -LiteralPath $canonical -Force -ErrorAction Stop
    $hash = Get-FileHash -LiteralPath $canonical -Algorithm SHA256 -ErrorAction Stop
    $identity = Get-ReadOnlyFileIdentity -Path $canonical
    [pscustomobject]@{
        Path = $canonical
        Format = [string]$vhd.VhdFormat
        Type = [string]$vhd.VhdType
        ParentPath = [string]$vhd.ParentPath
        VirtualDiskId = [string]$vhd.DiskIdentifier
        HasReparsePoint = (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        FileLength = [long]$item.Length
        VirtualSize = [long]$vhd.Size
        Sha256 = [string]$hash.Hash
        FileId = $identity.FileId
        LinkCount = $identity.LinkCount
        Attached = [bool]$vhd.Attached
    }
}

try {
    $vmGuid = [guid]::Empty
    if (-not [guid]::TryParse($VmId, [ref]$vmGuid) -or $vmGuid -eq [guid]::Empty) {
        throw "VmId must be an exact non-empty GUID."
    }

    # Read-only Hyper-V queries only. No Mount-VHD, Dismount-VHD, Set-VM,
    # Stop-VM, disk initialization, partitioning, formatting, or physical disk APIs.
    $vmResults = @(Get-VM -Id $vmGuid -ErrorAction Stop)
    if ($vmResults.Count -ne 1) { throw "Expected exactly one Hyper-V VM matching the supplied VM GUID." }
    $vm = $vmResults[0]
    $drives = @(Get-VMHardDiskDrive -VM $vm -ErrorAction Stop)

    # Capture the two operator-designated VHDX files first, then derive
    # attachment safety flags from their actual metadata rather than defaults.
    $boot = Get-VhdxEvidence -Path $BootVhdxPath
    $data = Get-VhdxEvidence -Path $DataVhdxPath
    $vhdxByPath = @{}
    $vhdxByPath[(Get-CanonicalPath $boot.Path).ToUpperInvariant()] = $boot
    $vhdxByPath[(Get-CanonicalPath $data.Path).ToUpperInvariant()] = $data

    $attachments = @()
    foreach ($drive in $drives) {
        foreach ($propertyName in @("Path", "ControllerType", "DiskNumber", "SupportPersistentReservations")) {
            if ($null -eq $drive.PSObject.Properties[$propertyName]) {
                throw "Hyper-V attachment metadata '$propertyName' is unavailable; refusing to infer a safe value."
            }
        }
        if ($null -eq $drive.SupportPersistentReservations -or $drive.SupportPersistentReservations -isnot [bool]) {
            throw "Hyper-V shared-disk metadata is null or not Boolean; refusing to infer that the disk is not shared."
        }
        if ([string]::IsNullOrWhiteSpace([string]$drive.Path)) {
            throw "A pass-through or pathless VM disk attachment was detected."
        }
        $attachmentPath = Get-CanonicalPath ([string]$drive.Path)
        $attachmentKey = $attachmentPath.ToUpperInvariant()
        if (-not $vhdxByPath.ContainsKey($attachmentKey)) {
            throw "An attached disk is not one of the two explicitly designated VHDX files."
        }
        $attachedVhdx = $vhdxByPath[$attachmentKey]
        $attachments += @{
            Path = $attachmentPath
            ControllerType = [string]$drive.ControllerType
            IsPassThrough = ($null -ne $drive.DiskNumber)
            IsShared = $drive.SupportPersistentReservations
            IsDifferencing = (-not [string]::IsNullOrWhiteSpace([string]$attachedVhdx.ParentPath))
        }
    }
    $inventory = @{
        Vm = @{
            Id = [string]$vm.Id
            Name = [string]$vm.Name
            Generation = [int]$vm.Generation
            State = [string]$vm.State
            ConfigurationVersion = [string]$vm.Version
        }
        Attachments = $attachments
        Vhdx = @{ Boot = $boot; Data = $data }
    }
    $expected = @{
        VmId = $VmId; VmName = $VmName
        BootVhdxPath = $BootVhdxPath; DataVhdxPath = $DataVhdxPath
        RunId = $RunId; Phase = $Phase
        FixtureLength = $FixtureLength; FixtureSha256 = $FixtureSha256
        BaselineLength = $BaselineLength; BaselineSha256 = $BaselineSha256
        BaselineFileId = $BaselineFileId
    }
    $result = Test-DiskRescueHyperVPreflightInventory -Expected $expected -Inventory $inventory

    $evidenceFullPath = [System.IO.Path]::GetFullPath($EvidencePath)
    if ($evidenceFullPath -notmatch '^[A-Za-z]:[\\/]') {
        throw "Evidence output must use a fully qualified local drive-letter path."
    }
    if ([string]::Equals($evidenceFullPath, $boot.Path, [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($evidenceFullPath, $data.Path, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence output must not overwrite either VHDX."
    }
    if (Test-Path -LiteralPath $evidenceFullPath) {
        throw "Evidence output already exists; refusing to overwrite: $evidenceFullPath"
    }
    if (Test-Path -LiteralPath $evidenceFullPath -PathType Container) {
        throw "Evidence output must be a new file path, not an existing directory."
    }
    if (-not (Test-Path -LiteralPath $evidenceFullPath -IsValid)) {
        throw "Evidence output path is invalid."
    }
    $evidenceDirectory = Split-Path -Parent $evidenceFullPath
    if (-not (Test-Path -LiteralPath $evidenceDirectory -PathType Container)) {
        throw "Evidence directory must already exist; this preflight will not create directories."
    }
    Assert-NoReparsePointInPath $evidenceDirectory
    if ([string]::Equals($evidenceFullPath, [System.IO.Path]::GetFullPath($PSScriptRoot), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence output must be a file path, not a directory."
    }

    $document = [ordered]@{
        Schema = "DiskRescue.HyperVPreflight.v1"
        Result = $result
        Vm = $inventory.Vm
        Attachments = $inventory.Attachments
        BootVhdx = $boot
        DataVhdx = $data
    }
    $json = $document | ConvertTo-Json -Depth 8
    $stream = [System.IO.File]::Open($evidenceFullPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + [Environment]::NewLine)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }

    Write-Output $result
    Write-Host "Read-only Hyper-V preflight evidence written: $evidenceFullPath"
    Write-Host "DestructiveActionAuthorized: false"
}
catch {
    Write-Error "PREFLIGHT NOT RUN / REJECTED: $_"
    exit 2
}

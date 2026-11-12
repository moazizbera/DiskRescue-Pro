#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "HyperVCrashPreflight.Validation.psm1") -Force

function New-Fixture {
    $vmId = "c9f88428-ae55-4db9-bcca-cf1e23bfa210"
    $boot = "C:/HyperV/Disposable/boot.vhdx"
    $data = "C:/HyperV/Disposable/data.vhdx"
    $expected = @{
        VmId = $vmId; VmName = "DiskRescue-CrashTest-Disposable"
        BootVhdxPath = $boot; DataVhdxPath = $data
        RunId = "preflight-run-20261112"; Phase = "ImageDataFlushedBeforeRename"
        FixtureLength = 4096L; FixtureSha256 = ("A" * 64)
        BaselineLength = 1048576L; BaselineSha256 = ("B" * 64)
        BaselineFileId = "volume-serial:00001234;file-id:000000000000abcd"
    }
    $inventory = @{
        Vm = @{ Id = $vmId; Name = $expected.VmName; Generation = 2; State = "Off"; ConfigurationVersion = "12.0" }
        Attachments = @(
            @{ Path = $boot; ControllerType = "SCSI"; IsPassThrough = $false; IsShared = $false; IsDifferencing = $false }
            @{ Path = $data; ControllerType = "SCSI"; IsPassThrough = $false; IsShared = $false; IsDifferencing = $false }
        )
        Vhdx = @{
            Boot = @{ Path = $boot; Format = "VHDX"; Type = "Dynamic"; ParentPath = $null; VirtualDiskId = "d9f88428-ae55-4db9-bcca-cf1e23bfa210"; HasReparsePoint = $false; FileLength = 8192L; VirtualSize = 1073741824L }
            Data = @{ Path = $data; Format = "VHDX"; Type = "Dynamic"; ParentPath = $null; VirtualDiskId = "e9f88428-ae55-4db9-bcca-cf1e23bfa210"; HasReparsePoint = $false; FileLength = 1048576L; VirtualSize = 1073741824L; Sha256 = ("B" * 64); FileId = $expected.BaselineFileId }
        }
    }
    return @{ Expected = $expected; Inventory = $inventory }
}

$passed = 0
$failed = 0
function Invoke-Case([string]$Name, [scriptblock]$Action, [bool]$ShouldPass) {
    try {
        & $Action
        if (-not $ShouldPass) { throw "Expected rejection but validation succeeded." }
        Write-Host "PASS $Name"; $script:passed++
    }
    catch {
        if ($ShouldPass) { Write-Host "FAIL $Name : $_"; $script:failed++ }
        elseif ($_.Exception.Message -eq "Expected rejection but validation succeeded.") { Write-Host "FAIL $Name : $_"; $script:failed++ }
        else { Write-Host "PASS $Name (rejected: $($_.Exception.Message))"; $script:passed++ }
    }
}

$f = New-Fixture
Invoke-Case "valid exact VM and two-disk inventory" { Test-DiskRescueHyperVPreflightInventory -Expected $f.Expected -Inventory $f.Inventory | Out-Null } $true

$cases = @(
    @{ Name="wrong VM ID"; Change={ param($x) $x.Inventory.Vm.Id="a9f88428-ae55-4db9-bcca-cf1e23bfa210" } }
    @{ Name="same name but different VM ID"; Change={ param($x) $x.Inventory.Vm.Id="b9f88428-ae55-4db9-bcca-cf1e23bfa210" } }
    @{ Name="boot/data path swap"; Change={ param($x) $x.Inventory.Vhdx.Boot.Path=$x.Expected.DataVhdxPath } }
    @{ Name="duplicate attachment"; Change={ param($x) $x.Inventory.Attachments[1].Path=$x.Expected.BootVhdxPath } }
    @{ Name="unexpected third VHDX"; Change={ param($x) $x.Inventory.Attachments += @{ Path="C:/HyperV/extra.vhdx"; ControllerType="SCSI"; IsPassThrough=$false; IsShared=$false; IsDifferencing=$false } } }
    @{ Name="missing attachment"; Change={ param($x) $x.Inventory.Attachments=@($x.Inventory.Attachments[0]) } }
    @{ Name="differencing data disk"; Change={ param($x) $x.Inventory.Vhdx.Data.ParentPath="C:/HyperV/parent.vhdx" } }
    @{ Name="changed baseline hash"; Change={ param($x) $x.Inventory.Vhdx.Data.Sha256=("C" * 64) } }
    @{ Name="changed file identity"; Change={ param($x) $x.Inventory.Vhdx.Data.FileId="changed-file-id" } }
    @{ Name="UNC path"; Change={ param($x) $x.Expected.DataVhdxPath="//server/share/data.vhdx" } }
    @{ Name="reparse-point path"; Change={ param($x) $x.Inventory.Vhdx.Data.HasReparsePoint=$true } }
    @{ Name="stale run ID"; Change={ param($x) $x.Expected.RunId="old" } }
    @{ Name="mismatched phase"; Change={ param($x) $x.Expected.Phase="OtherPhase" } }
    @{ Name="missing Hyper-V VM inspection"; Change={ param($x) $x.Inventory.Vm=$null } }
)
foreach ($case in $cases) {
    $f = New-Fixture
    & $case.Change $f
    Invoke-Case $case.Name { Test-DiskRescueHyperVPreflightInventory -Expected $f.Expected -Inventory $f.Inventory | Out-Null } $false
}
Write-Host "RESULT passed=$passed failed=$failed total=$($passed + $failed)"
if ($failed -ne 0) { exit 1 }

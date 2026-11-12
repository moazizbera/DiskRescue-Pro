Set-StrictMode -Version Latest

function ConvertTo-DiskRescueCanonicalLocalPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { throw "A required path is empty." }
    if ($Path -match '^(\\\\|//)' -or $Path -match '^[a-zA-Z]+:') {
        if ($Path -match '^(\\\\|//)') { throw "UNC/network paths are not permitted: $Path" }
    }
    if ($Path -notmatch '^[a-zA-Z]:[\\/]') {
        throw "Only fully qualified drive-letter local paths are permitted: $Path"
    }
    if ($Path -match '[*?]') { throw "Wildcard paths are not permitted: $Path" }
    $full = [System.IO.Path]::GetFullPath($Path)
    return $full.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
}

function Assert-DiskRescueEqual {
    param([object]$Actual, [object]$Expected, [string]$Label)
    if ([string]$Actual -cne [string]$Expected) {
        throw "$Label mismatch. Expected '$Expected'; received '$Actual'."
    }
}

function Test-DiskRescueHyperVPreflightInventory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Expected,
        [Parameter(Mandatory)][hashtable]$Inventory
    )

    $required = @(
        "VmId", "VmName", "BootVhdxPath", "DataVhdxPath", "RunId",
        "Phase", "FixtureLength", "FixtureSha256", "BaselineLength",
        "BaselineSha256", "BaselineFileId"
    )
    foreach ($key in $required) {
        if (-not $Expected.ContainsKey($key) -or $null -eq $Expected[$key] -or [string]::IsNullOrWhiteSpace([string]$Expected[$key])) {
            throw "Expected input '$key' is required."
        }
    }

    $vmGuid = [guid]::Empty
    if (-not [guid]::TryParse([string]$Expected.VmId, [ref]$vmGuid) -or $vmGuid -eq [guid]::Empty) {
        throw "VmId must be an exact non-empty GUID."
    }
    if ([string]$Expected.RunId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{7,127}$') {
        throw "RunId must be a unique, explicit 8-128 character token."
    }
    if ([string]$Expected.Phase -notin @(
        "ImageDataFlushedBeforeRename",
        "ImageRenamedBeforeNamespaceFlush",
        "ImageNamespaceFlushed",
        "ManifestRenamedBeforeNamespaceFlush",
        "ManifestNamespaceFlushed"
    )) { throw "Phase is not a supported writer checkpoint." }
    if ([long]$Expected.FixtureLength -le 0 -or [long]$Expected.BaselineLength -le 0) {
        throw "Fixture and baseline lengths must be positive."
    }
    foreach ($hashKey in @("FixtureSha256", "BaselineSha256")) {
        if ([string]$Expected[$hashKey] -notmatch '^[A-Fa-f0-9]{64}$') {
            throw "$hashKey must be a 64-character SHA-256 value."
        }
    }

    $bootPath = ConvertTo-DiskRescueCanonicalLocalPath ([string]$Expected.BootVhdxPath)
    $dataPath = ConvertTo-DiskRescueCanonicalLocalPath ([string]$Expected.DataVhdxPath)
    if ([string]::Equals($bootPath, $dataPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Boot and data VHDX paths must be distinct."
    }

    $vm = $Inventory.Vm
    if ($null -eq $vm) { throw "VM inspection result is missing." }
    Assert-DiskRescueEqual $vm.Id $vmGuid.ToString() "VM ID"
    Assert-DiskRescueEqual $vm.Name $Expected.VmName "VM name"
    if ($null -eq $vm.Generation -or $null -eq $vm.State -or $null -eq $vm.ConfigurationVersion) {
        throw "VM generation, state, and configuration version are required evidence."
    }
    if ([string]$vm.State -ne "Off") {
        throw "The disposable VM must be Off while the baseline is captured; observed state '$($vm.State)'."
    }

    $attachments = @($Inventory.Attachments)
    if ($attachments.Count -ne 2) {
        throw "Exactly two VHDX attachments are required (one boot and one data); found $($attachments.Count)."
    }
    $canonicalAttachments = @()
    foreach ($attachment in $attachments) {
        foreach ($field in @("Path", "ControllerType", "IsPassThrough", "IsShared", "IsDifferencing")) {
            if ($null -eq $attachment.PSObject.Properties[$field] -and
                ($attachment -isnot [System.Collections.IDictionary] -or -not $attachment.Contains($field))) {
                throw "Attachment metadata '$field' is missing."
            }
        }
        foreach ($flag in @("IsPassThrough", "IsShared", "IsDifferencing")) {
            $flagValue = if ($attachment -is [System.Collections.IDictionary]) {
                $attachment[$flag]
            } else {
                $attachment.PSObject.Properties[$flag].Value
            }
            if ($flagValue -isnot [bool]) {
                throw "Attachment safety metadata '$flag' must be an explicit Boolean value."
            }
        }
        if ($attachment.ControllerType -notin @("IDE", "SCSI")) { throw "Unexpected controller type." }
        if ($attachment.IsPassThrough -or $attachment.IsShared -or $attachment.IsDifferencing) {
            throw "Pass-through, shared, and differencing attachments are not permitted."
        }
        $canonicalAttachments += ConvertTo-DiskRescueCanonicalLocalPath ([string]$attachment.Path)
    }
    if (@($canonicalAttachments | Select-Object -Unique).Count -ne 2) {
        throw "Duplicate VHDX attachment paths are not permitted."
    }
    if (@($canonicalAttachments | Where-Object { [string]::Equals($_, $bootPath, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 1) {
        throw "The exact expected boot VHDX attachment was not found."
    }
    if (@($canonicalAttachments | Where-Object { [string]::Equals($_, $dataPath, [StringComparison]::OrdinalIgnoreCase) }).Count -ne 1) {
        throw "The exact expected data VHDX attachment was not found."
    }

    foreach ($role in @("Boot", "Data")) {
        $path = if ($role -eq "Boot") { $bootPath } else { $dataPath }
        $disk = $Inventory.Vhdx[$role]
        if ($null -eq $disk) { throw "$role VHDX metadata is missing." }
        foreach ($field in @("Path", "Format", "Type", "ParentPath", "VirtualDiskId", "HasReparsePoint", "FileLength", "VirtualSize", "Sha256", "FileId", "LinkCount")) {
            $present = if ($disk -is [System.Collections.IDictionary]) {
                $disk.Contains($field)
            } else {
                $null -ne $disk.PSObject.Properties[$field]
            }
            if (-not $present) { throw "$role VHDX metadata '$field' is missing." }
        }
        $reparseValue = if ($disk -is [System.Collections.IDictionary]) { $disk["HasReparsePoint"] } else { $disk.PSObject.Properties["HasReparsePoint"].Value }
        if ($reparseValue -isnot [bool]) { throw "$role VHDX reparse-point status must be an explicit Boolean value." }
        Assert-DiskRescueEqual (ConvertTo-DiskRescueCanonicalLocalPath ([string]$disk.Path)) $path "$role VHDX path"
        Assert-DiskRescueEqual $disk.Format "VHDX" "$role VHDX format"
        if ($disk.Type -notin @("Fixed", "Dynamic")) { throw "$role VHDX type is unsupported." }
        if ($disk.ParentPath) { throw "$role VHDX must be a standalone disk without a parent." }
        $diskGuid = [guid]::Empty
        if (-not [guid]::TryParse([string]$disk.VirtualDiskId, [ref]$diskGuid) -or $diskGuid -eq [guid]::Empty) {
            throw "$role VHDX virtual-disk ID is invalid."
        }
        if ([bool]$disk.HasReparsePoint) { throw "$role VHDX path contains a reparse point." }
        if ([long]$disk.FileLength -le 0 -or [long]$disk.VirtualSize -le 0) { throw "$role VHDX sizes are invalid." }
        if ([string]::IsNullOrWhiteSpace([string]$disk.FileId)) { throw "$role VHDX file identity is missing." }
        if ([string]$disk.Sha256 -notmatch '^[A-Fa-f0-9]{64}
        $linkCount = 0L
        if ($disk -is [System.Collections.IDictionary]) {
            $linkCountPresent = $disk.Contains("LinkCount")
            if ($linkCountPresent) {
                if ($null -eq $disk["LinkCount"]) { throw "$role VHDX hard-link count is unknown." }
                $linkCount = [long]$disk["LinkCount"]
            }
        }
        else {
            $linkCountProperty = $disk.PSObject.Properties["LinkCount"]
            $linkCountPresent = ($null -ne $linkCountProperty)
            if ($linkCountPresent) {
                if ($null -eq $linkCountProperty.Value) { throw "$role VHDX hard-link count is unknown." }
                $linkCount = [long]$linkCountProperty.Value
            }
        }
        if (-not $linkCountPresent) { throw "$role VHDX hard-link count evidence is missing." }
        if ($linkCount -ne 1) {
            throw "$role VHDX must not have additional hard-link aliases."
        }
    }

    $bootId = [string]$Inventory.Vhdx.Boot.VirtualDiskId
    $dataId = [string]$Inventory.Vhdx.Data.VirtualDiskId
    if ([string]::Equals($bootId, $dataId, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Boot and data VHDX virtual-disk identities must be distinct."
    }

    $data = $Inventory.Vhdx.Data
    Assert-DiskRescueEqual $data.FileLength $Expected.BaselineLength "Data VHDX baseline length"
    Assert-DiskRescueEqual ([string]$data.Sha256).ToUpperInvariant() ([string]$Expected.BaselineSha256).ToUpperInvariant() "Data VHDX baseline SHA-256"
    Assert-DiskRescueEqual $data.FileId $Expected.BaselineFileId "Data VHDX baseline file identity"

    [pscustomobject]@{
        Status = "PreflightValidated"
        VmId = $vmGuid.ToString()
        VmName = [string]$vm.Name
        Generation = [int]$vm.Generation
        State = [string]$vm.State
        ConfigurationVersion = [string]$vm.ConfigurationVersion
        BootVhdxPath = $bootPath
        DataVhdxPath = $dataPath
        RunId = [string]$Expected.RunId
        Phase = [string]$Expected.Phase
        FixtureLength = [long]$Expected.FixtureLength
        FixtureSha256 = ([string]$Expected.FixtureSha256).ToUpperInvariant()
        DataBaselineLength = [long]$data.FileLength
        DataBaselineSha256 = ([string]$data.Sha256).ToUpperInvariant()
        DataBaselineFileId = [string]$data.FileId
        ValidatedAtUtc = [DateTime]::UtcNow.ToString("O")
        DestructiveActionAuthorized = $false
    }
}

Export-ModuleMember -Function Test-DiskRescueHyperVPreflightInventory
) { throw "$role VHDX SHA-256 evidence is invalid." }
        $linkCountPresent = $false
        $linkCount = 0L
        if ($disk -is [System.Collections.IDictionary]) {
            $linkCountPresent = $disk.Contains("LinkCount")
            if ($linkCountPresent) {
                if ($null -eq $disk["LinkCount"]) { throw "$role VHDX hard-link count is unknown." }
                $linkCount = [long]$disk["LinkCount"]
            }
        }
        else {
            $linkCountProperty = $disk.PSObject.Properties["LinkCount"]
            $linkCountPresent = ($null -ne $linkCountProperty)
            if ($linkCountPresent) {
                if ($null -eq $linkCountProperty.Value) { throw "$role VHDX hard-link count is unknown." }
                $linkCount = [long]$linkCountProperty.Value
            }
        }
        if (-not $linkCountPresent) { throw "$role VHDX hard-link count evidence is missing." }
        if ($linkCount -ne 1) {
            throw "$role VHDX must not have additional hard-link aliases."
        }
    }

    $bootId = [string]$Inventory.Vhdx.Boot.VirtualDiskId
    $dataId = [string]$Inventory.Vhdx.Data.VirtualDiskId
    if ([string]::Equals($bootId, $dataId, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Boot and data VHDX virtual-disk identities must be distinct."
    }

    $data = $Inventory.Vhdx.Data
    Assert-DiskRescueEqual $data.FileLength $Expected.BaselineLength "Data VHDX baseline length"
    Assert-DiskRescueEqual ([string]$data.Sha256).ToUpperInvariant() ([string]$Expected.BaselineSha256).ToUpperInvariant() "Data VHDX baseline SHA-256"
    Assert-DiskRescueEqual $data.FileId $Expected.BaselineFileId "Data VHDX baseline file identity"

    [pscustomobject]@{
        Status = "PreflightValidated"
        VmId = $vmGuid.ToString()
        VmName = [string]$vm.Name
        Generation = [int]$vm.Generation
        State = [string]$vm.State
        ConfigurationVersion = [string]$vm.ConfigurationVersion
        BootVhdxPath = $bootPath
        DataVhdxPath = $dataPath
        RunId = [string]$Expected.RunId
        Phase = [string]$Expected.Phase
        FixtureLength = [long]$Expected.FixtureLength
        FixtureSha256 = ([string]$Expected.FixtureSha256).ToUpperInvariant()
        DataBaselineLength = [long]$data.FileLength
        DataBaselineSha256 = ([string]$data.Sha256).ToUpperInvariant()
        DataBaselineFileId = [string]$data.FileId
        ValidatedAtUtc = [DateTime]::UtcNow.ToString("O")
        DestructiveActionAuthorized = $false
    }
}

Export-ModuleMember -Function Test-DiskRescueHyperVPreflightInventory

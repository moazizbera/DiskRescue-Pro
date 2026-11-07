[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$sectorSize = 512
$clusterSize = 512
$totalSectors = 128
$volumeSectors = 127
$image = New-Object byte[] ($sectorSize * $totalSectors)
$boot = New-Object byte[] $sectorSize

function Write-U16([byte[]]$Bytes, [int]$Offset, [UInt16]$Value) {
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Bytes, $Offset, 2)
}
function Write-U32([byte[]]$Bytes, [int]$Offset, [UInt32]$Value) {
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Bytes, $Offset, 4)
}
function Write-U64([byte[]]$Bytes, [int]$Offset, [UInt64]$Value) {
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Bytes, $Offset, 8)
}

# Minimal synthetic NTFS boot sector. This is a parser fixture, not a mountable volume.
$image[0] = 0xEB
$image[1] = 0x52
$image[2] = 0x90
[Text.Encoding]::ASCII.GetBytes("NTFS    ").CopyTo($boot, 3)
Write-U16 $boot 11 $sectorSize
$boot[13] = 1
$boot[21] = 0xF8
Write-U64 $boot 40 $volumeSectors
Write-U64 $boot 48 4
Write-U64 $boot 56 8
$boot[64] = 0xF6 # -10: 2^10 = 1024-byte FILE record
$boot[68] = 0xF6 # -10: 1024-byte index buffer
Write-U64 $boot 72 0x1122334455667788
$boot[510] = 0x55
$boot[511] = 0xAA

# MBR entry exposes the synthetic NTFS volume to the desktop partition selector.
$image[446 + 4] = 0x07
Write-U32 $image 454 1
Write-U32 $image 458 $volumeSectors
$image[510] = 0x55
$image[511] = 0xAA
[Array]::Copy($boot, 0, $image, $sectorSize, $sectorSize)

function New-FileRecord([UInt32]$RecordNumber, [bool]$IncludeMftData, [bool]$IncludeFileName) {
    $record = New-Object byte[] 1024
    [Text.Encoding]::ASCII.GetBytes("FILE").CopyTo($record, 0)
    Write-U16 $record 4 48
    Write-U16 $record 6 3
    Write-U16 $record 16 9
    Write-U16 $record 18 1
    Write-U16 $record 20 56
    Write-U16 $record 22 1
    Write-U32 $record 28 1024
    Write-U32 $record 44 $RecordNumber

    Write-U16 $record 48 0x5A5A
    Write-U16 $record 50 0xAAAA
    Write-U16 $record 52 0xBBBB
    Write-U16 $record 510 0x5A5A
    Write-U16 $record 1022 0x5A5A

    if ($IncludeMftData) {
        # Unnamed non-resident $MFT::$DATA: VCN 0..2 at LCN 4, VCN 3..7 at LCN 12.
        Write-U32 $record 56 0x80
        Write-U32 $record 60 72
        $record[64] = 1
        Write-U64 $record 72 0
        Write-U64 $record 80 7
        Write-U16 $record 88 64
        Write-U64 $record 96 4096
        Write-U64 $record 104 4096
        Write-U64 $record 112 4096
        $record[120] = 0x11
        $record[121] = 3
        $record[122] = 4
        $record[123] = 0x11
        $record[124] = 5
        $record[125] = 8
        $record[126] = 0
        Write-U32 $record 128 4294967295
        Write-U32 $record 24 132
    }
    elseif ($IncludeFileName) {
        # Resident $FILE_NAME value for Report.txt, parent record 5 sequence 2.
        Write-U32 $record 56 0x30
        Write-U32 $record 60 112
        $record[64] = 0
        Write-U16 $record 70 1
        Write-U32 $record 72 86
        Write-U16 $record 76 24
        Write-U64 $record 80 562949953421317
        Write-U64 $record 120 4096
        Write-U64 $record 128 2048
        Write-U32 $record 136 0x20
        $record[144] = 10
        $record[145] = 1
        [Text.Encoding]::Unicode.GetBytes("Report.txt").CopyTo($record, 146)
        # Add a small resident $DATA stream so the extraction CLI can be smoke-tested.
        $payload = [Text.Encoding]::UTF8.GetBytes("DiskRescue synthetic recovered content")
        $dataAttributeLength = (24 + $payload.Length + 7) -band -8
        Write-U32 $record 168 0x80
        Write-U32 $record 172 $dataAttributeLength
        $record[176] = 0
        Write-U16 $record 182 2
        Write-U32 $record 184 $payload.Length
        Write-U16 $record 188 24
        [Array]::Copy($payload, 0, $record, 192, $payload.Length)
        $endMarkerOffset = 168 + $dataAttributeLength
        Write-U32 $record $endMarkerOffset 4294967295
        Write-U32 $record 24 ($endMarkerOffset + 4)
    }
    else {
        Write-U32 $record 56 4294967295
        Write-U32 $record 24 60
    }
    return ,$record
}

# Place MFT record 0 at its boot-sector LCN.
$record0 = New-FileRecord 0 $true $false
[Array]::Copy($record0, 0, $image, $sectorSize + 4 * $clusterSize, 1024)

# Record 1 deliberately crosses a fragmented extent boundary: logical VCN 2 is LCN 6,
# and logical VCN 3 is LCN 12.
$record1 = New-FileRecord 1 $false $true
[Array]::Copy($record1, 0, $image, $sectorSize + 6 * $clusterSize, 512)
[Array]::Copy($record1, 512, $image, $sectorSize + 12 * $clusterSize, 512)

$record2 = New-FileRecord 2 $false $false
[Array]::Copy($record2, 0, $image, $sectorSize + 13 * $clusterSize, 1024)
$record3 = New-FileRecord 3 $false $false
[Array]::Copy($record3, 0, $image, $sectorSize + 15 * $clusterSize, 1024)

$fullPath = [IO.Path]::GetFullPath($OutputPath)
$directory = [IO.Path]::GetDirectoryName($fullPath)
if (-not [IO.Directory]::Exists($directory)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
[IO.File]::WriteAllBytes($fullPath, $image)
$hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
Write-Output "Created synthetic NTFS MFT preview image: $fullPath"
Write-Output "Length: $($image.Length) bytes; NTFS partition: offset $sectorSize bytes, length $($volumeSectors * $sectorSize) bytes; SHA256: $hash"
Write-Output 'Fixture contains one FILE_NAME entry (Report.txt), a small resident $DATA payload, and a fragmented $MFT data extent.'
Write-Output "This fixture is for parser tests only and is not a mountable NTFS volume."

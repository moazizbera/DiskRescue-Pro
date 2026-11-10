[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [switch]$MarkDataClustersAllocated,
    [switch]$Fragmented
)

$ErrorActionPreference = "Stop"
$sectorSize = 512
$clusterSize = 512
$volumeSectors = 127
$image = New-Object byte[] ($sectorSize * ($volumeSectors + 1))
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

# A disposable, minimal NTFS parser fixture. The volume starts at sector 1.
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
$boot[64] = 0xF6
$boot[68] = 0xF6
Write-U64 $boot 72 0x1122334455667788
$boot[510] = 0x55
$boot[511] = 0xAA

# MBR entry for the synthetic NTFS volume.
$image[446 + 4] = 0x07
Write-U32 $image 454 1
Write-U32 $image 458 $volumeSectors
$image[510] = 0x55
$image[511] = 0xAA
[Array]::Copy($boot, 0, $image, $sectorSize, $sectorSize)

function New-FileRecord([UInt32]$RecordNumber, [UInt16]$Flags, [string]$Kind, [UInt64]$BaseReference = 0) {
    $record = New-Object byte[] 1024
    [Text.Encoding]::ASCII.GetBytes("FILE").CopyTo($record, 0)
    Write-U16 $record 4 48
    Write-U16 $record 6 3
    Write-U16 $record 16 9
    Write-U16 $record 18 1
    Write-U16 $record 20 56
    Write-U16 $record 22 $Flags
    Write-U32 $record 28 1024
    Write-U32 $record 44 $RecordNumber
    Write-U64 $record 32 $BaseReference
    Write-U16 $record 48 0x5A5A
    Write-U16 $record 50 0xAAAA
    Write-U16 $record 52 0xBBBB
    Write-U16 $record 510 0x5A5A
    Write-U16 $record 1022 0x5A5A

    if ($Kind -eq "Mft") {
        # One contiguous $MFT::$DATA extent: VCN 0..15 at LCN 4.
        Write-U32 $record 56 0x80
        Write-U32 $record 60 72
        $record[64] = 1
            Write-U16 $record 70 1
        Write-U64 $record 72 0
        Write-U64 $record 80 15
        Write-U16 $record 88 64
        Write-U64 $record 96 8192
        Write-U64 $record 104 8192
        Write-U64 $record 112 8192
        $record[120] = 0x11
        $record[121] = 16
        $record[122] = 4
        $record[123] = 0
        Write-U32 $record 128 4294967295
        Write-U32 $record 24 132
    }
    elseif ($Kind -eq "DeletedFile") {
        if ($Fragmented) {
            # Base extent VCN 0 at LCN 30; continuation extent VCN 1 at LCN 34.
            Write-U32 $record 56 0x80; Write-U32 $record 60 72; $record[64] = 1
            Write-U16 $record 70 1
            Write-U64 $record 72 0; Write-U64 $record 80 0; Write-U16 $record 88 64
            Write-U64 $record 96 1024; Write-U64 $record 104 1024; Write-U64 $record 112 1024
            $record[120] = 0x11; $record[121] = 1; $record[122] = 30; $record[123] = 0
        } else {
            # Existing single-extent fixture: two clusters at LCN 30..31.
            Write-U32 $record 56 0x80; Write-U32 $record 60 72; $record[64] = 1
            Write-U16 $record 70 1
            Write-U64 $record 72 0; Write-U64 $record 80 1; Write-U16 $record 88 64
            Write-U64 $record 96 1024; Write-U64 $record 104 512; Write-U64 $record 112 512
            $record[120] = 0x11; $record[121] = 2; $record[122] = 30; $record[123] = 0
        }
        # Resident $FILE_NAME attribute references active parent record 5, sequence 9.
        $name = "Deleted.txt"; $nameBytes = [Text.Encoding]::Unicode.GetBytes($name)
        $valueLength = 66 + $nameBytes.Length
        $attributeLength = (24 + $valueLength + 7) -band -8
        Write-U32 $record 128 0x30; Write-U32 $record 132 $attributeLength; $record[136] = 0
        Write-U32 $record 144 $valueLength; Write-U16 $record 148 24
        Write-U64 $record 152 (([UInt64]9 -shl 48) -bor [UInt64]5)
        Write-U64 $record 192 4096; Write-U64 $record 200 2048
        $record[216] = [byte]$name.Length; $record[217] = 1
        [Array]::Copy($nameBytes, 0, $record, 218, $nameBytes.Length)
        $endMarkerOffset = 128 + $attributeLength
        if ($Fragmented) {
            # Resident $ATTRIBUTE_LIST contains exact identities for both deleted extents.
            $listOffset = $endMarkerOffset; $entryLength = 32; $listAttributeLength = 88
            Write-U32 $record $listOffset 0x20; Write-U32 $record ($listOffset + 4) $listAttributeLength
            Write-U32 $record ($listOffset + 16) 64; Write-U16 $record ($listOffset + 20) 24
            $firstEntry = $listOffset + 24
            Write-U32 $record $firstEntry 0x80; Write-U16 $record ($firstEntry + 4) $entryLength
            Write-U64 $record ($firstEntry + 8) 0
            Write-U64 $record ($firstEntry + 16) (([UInt64]9 -shl 48) -bor [UInt64]1)
            Write-U16 $record ($firstEntry + 24) 1
            $secondEntry = $firstEntry + $entryLength
            Write-U32 $record $secondEntry 0x80; Write-U16 $record ($secondEntry + 4) $entryLength
            Write-U64 $record ($secondEntry + 8) 1
            Write-U64 $record ($secondEntry + 16) (([UInt64]9 -shl 48) -bor [UInt64]2)
            Write-U16 $record ($secondEntry + 24) 1
            $endMarkerOffset = $listOffset + $listAttributeLength
        }
        Write-U32 $record $endMarkerOffset 4294967295
        Write-U32 $record 24 ($endMarkerOffset + 4)
    }
    elseif ($Kind -eq "DeletedContinuation") {
        # Deleted extension record 2 points back to base record 1, sequence 9.
        Write-U32 $record 56 0x80; Write-U32 $record 60 72; $record[64] = 1
            Write-U16 $record 70 1
        Write-U64 $record 72 1; Write-U64 $record 80 1; Write-U16 $record 88 64
        Write-U64 $record 96 1024; Write-U64 $record 104 1024; Write-U64 $record 112 1024
        $record[120] = 0x11; $record[121] = 1; $record[122] = 34; $record[123] = 0
        Write-U32 $record 128 4294967295; Write-U32 $record 24 132
    }
    elseif ($Kind -eq "Bitmap") {
        # Resident $Bitmap data; 16 bytes cover the 127-cluster volume.
        $bitmap = New-Object byte[] 16
        if ($MarkDataClustersAllocated) {
            if ($Fragmented) {
                $bitmap[3] = 0x40 # LCN 30 is allocated.
                $bitmap[4] = 0x04 # LCN 34 is allocated in the continuation extent.
            } else {
                $bitmap[3] = 0xC0 # LCN 30 and 31 are allocated.
            }
        }
        $attributeLength = 40
        Write-U32 $record 56 0x80
        Write-U32 $record 60 $attributeLength
        $record[64] = 0
        Write-U16 $record 70 1
        Write-U32 $record 72 $bitmap.Length
        Write-U16 $record 76 24
        [Array]::Copy($bitmap, 0, $record, 80, $bitmap.Length)
        Write-U32 $record 96 4294967295
        Write-U32 $record 24 100
    }
    else {
        Write-U32 $record 56 4294967295
        Write-U32 $record 24 60
    }
    return ,$record
}

# $MFT base record at LCN 4; record 1 at LCN 6; parent record 5 at LCN 14;
# $Bitmap record 6 at LCN 16. These locations are within this generated image.
$mft = New-FileRecord 0 1 "Mft"
$file = New-FileRecord 1 0 "DeletedFile"
$parent = New-FileRecord 5 3 "Empty"
$bitmap = New-FileRecord 6 1 "Bitmap"
$volumeBase = $sectorSize
[Array]::Copy($mft, 0, $image, $volumeBase + 4 * $clusterSize, 1024)
[Array]::Copy($file, 0, $image, $volumeBase + 6 * $clusterSize, 1024)
if ($Fragmented) {
    $baseReference = ([UInt64]9 -shl 48) -bor [UInt64]1
    $continuation = New-FileRecord 2 0 "DeletedContinuation" $baseReference
    [Array]::Copy($continuation, 0, $image, $volumeBase + 8 * $clusterSize, 1024)
}
[Array]::Copy($parent, 0, $image, $volumeBase + 14 * $clusterSize, 1024)
[Array]::Copy($bitmap, 0, $image, $volumeBase + 16 * $clusterSize, 1024)

# Deterministic payload split across physically fragmented clusters.
if ($Fragmented) {
    $payload = New-Object byte[] 1024
    for ($i = 0; $i -lt $payload.Length; $i++) { $payload[$i] = [byte]($i % 251) }
    [Array]::Copy($payload, 0, $image, $volumeBase + 30 * $clusterSize, 512)
    [Array]::Copy($payload, 512, $image, $volumeBase + 34 * $clusterSize, 512)
} else {
    $payload = New-Object byte[] 512
    for ($i = 0; $i -lt $payload.Length; $i++) { $payload[$i] = [byte]($i % 251) }
    [Array]::Copy($payload, 0, $image, $volumeBase + 30 * $clusterSize, $payload.Length)
}

$fullPath = [IO.Path]::GetFullPath($OutputPath)
$directory = [IO.Path]::GetDirectoryName($fullPath)
if (-not [IO.Directory]::Exists($directory)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
if ([IO.File]::Exists($fullPath)) {
    throw "Refusing to overwrite an existing synthetic image: $fullPath"
}
[IO.File]::WriteAllBytes($fullPath, $image)
$hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
Write-Output "Created synthetic deleted-NTFS fixture: $fullPath"
Write-Output "Volume offset: 512 bytes; volume length: $($volumeSectors * $sectorSize) bytes; deleted record: 1; parent: 5; payload: $(if ($Fragmented) { 1024 } else { 512 }) bytes"
Write-Output "Data clusters: $(if ($Fragmented) { '30 and 34 (fragmented)' } else { '30..31' }); allocation: $(if ($MarkDataClustersAllocated) { 'allocated for refusal test' } else { 'free for bounded-read test' })"
Write-Output "SHA256: $hash"
Write-Output "Disposable parser fixture only; no physical disk was accessed."

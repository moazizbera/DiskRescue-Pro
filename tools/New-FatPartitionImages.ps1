param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Write-U16([byte[]]$Bytes, [int]$Offset, [int]$Value) {
    [BitConverter]::GetBytes([uint16]$Value).CopyTo($Bytes, $Offset)
}
function Write-U32([byte[]]$Bytes, [int]$Offset, [uint32]$Value) {
    [BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}
function Write-U64([byte[]]$Bytes, [int]$Offset, [uint64]$Value) {
    [BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}
function New-Fat32Image {
    $sectorSize = 512
    $totalSectors = 70000
    $volumeOffset = 1MB
    $bytes = [byte[]]::new($volumeOffset + ($totalSectors * $sectorSize))
    foreach ($sector in @(0, 6)) {
        $offset = $volumeOffset + ($sector * $sectorSize)
        $boot = [byte[]]::new($sectorSize)
        $boot[0] = 0xEB; $boot[2] = 0x90
        Write-U16 $boot 11 512
        $boot[13] = 1
        Write-U16 $boot 14 32
        $boot[16] = 2
        Write-U32 $boot 32 $totalSectors
        Write-U32 $boot 36 600
        Write-U32 $boot 44 2
        Write-U16 $boot 48 1
        Write-U16 $boot 50 6
        Write-U32 $boot 67 0x12345678
        $boot[510] = 0x55; $boot[511] = 0xAA
        [Array]::Copy($boot, 0, $bytes, $offset, $boot.Length)
    }
    $path = Join-Path $OutputDirectory "lost-fat32-partition.img"
    [IO.File]::WriteAllBytes($path, $bytes)
    return $path
}
function New-ExFatImage {
    $sectorSize = 512
    $volumeOffset = 1MB
    $volumeSectors = 26
    $bytes = [byte[]]::new($volumeOffset + ($volumeSectors * $sectorSize))
    foreach ($copy in @(0, 1)) {
        $regionOffset = $volumeOffset + ($copy * 12 * $sectorSize)
        $boot = [byte[]]::new($sectorSize)
        $boot[0] = 0xEB; $boot[1] = 0x76; $boot[2] = 0x90
        [Text.Encoding]::ASCII.GetBytes("EXFAT   ").CopyTo($boot, 3)
        Write-U64 $boot 72 $volumeSectors
        Write-U32 $boot 80 24
        Write-U32 $boot 84 1
        Write-U32 $boot 88 25
        Write-U32 $boot 92 1
        Write-U32 $boot 96 2
        $boot[108] = 9; $boot[109] = 0; $boot[110] = 1; $boot[112] = 0xFF
        $boot[510] = 0x55; $boot[511] = 0xAA
        [Array]::Copy($boot, 0, $bytes, $regionOffset, $boot.Length)
        for ($sector = 1; $sector -le 8; $sector++) {
            $extendedOffset = $regionOffset + ($sector * $sectorSize)
            $bytes[$extendedOffset + 510] = 0x55
            $bytes[$extendedOffset + 511] = 0xAA
        }
        [uint32]$sum = 0
        for ($i = 0; $i -lt (11 * $sectorSize); $i++) {
            if ($i -eq 106 -or $i -eq 107 -or $i -eq 112) { continue }
            $sum = (($sum -shr 1) -bor (($sum -band 1) -shl 31))
            $sum = [uint32](($sum + $bytes[$regionOffset + $i]) -band 0xFFFFFFFFL)
        }
        for ($i = 0; $i -lt $sectorSize; $i += 4) {
            [BitConverter]::GetBytes($sum).CopyTo($bytes, $regionOffset + (11 * $sectorSize) + $i)
        }
    }
    $path = Join-Path $OutputDirectory "lost-exfat-partition.img"
    [IO.File]::WriteAllBytes($path, $bytes)
    return $path
}
$fat32 = New-Fat32Image
$exfat = New-ExFatImage
Write-Output "Created synthetic FAT32 fixture: $fat32"
Write-Output "Created synthetic exFAT fixture: $exfat"

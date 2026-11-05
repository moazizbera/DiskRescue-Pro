[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PWD "DiskRescue-TestImages")
)

$ErrorActionPreference = "Stop"
$resolved = [IO.Path]::GetFullPath($OutputDirectory)
$root = [IO.Path]::GetPathRoot($resolved)
if ($resolved.TrimEnd('\','/') -eq $root.TrimEnd('\','/')) { throw "Refusing to use a drive root." }
if (-not (Test-Path -LiteralPath $resolved)) { New-Item -ItemType Directory -Path $resolved | Out-Null }
if (-not (Test-Path -LiteralPath $resolved -PathType Container)) { throw "Output path is not a directory." }

function Set-U32([byte[]]$Buffer, [int]$Offset, [UInt32]$Value) {
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Buffer, $Offset, 4)
}
function Set-U64([byte[]]$Buffer, [int]$Offset, [UInt64]$Value) {
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Buffer, $Offset, 8)
}
function Get-Crc32([byte[]]$Data) {
    [UInt32]$crc = 0xFFFFFFFF
    foreach ($value in $Data) {
        $crc = $crc -bxor [UInt32]$value
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) { $crc = ($crc -shr 1) -bxor [UInt32]0xEDB88320 }
            else { $crc = $crc -shr 1 }
        }
    }
    return [UInt32]($crc -bxor [UInt32]0xFFFFFFFF)
}
function Save-NewImage([string]$Name, [byte[]]$Bytes) {
    $path = Join-Path $resolved $Name
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length) } finally { $stream.Dispose() }
    [pscustomobject]@{ File = $path; Bytes = $Bytes.Length; SHA256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}

$sectorSize = 512
$imageBytes = 100 * $sectorSize
$mbr = New-Object byte[] $imageBytes
$mbr[450] = 0x07
Set-U32 $mbr 454 1
Set-U32 $mbr 458 20
$mbr[510] = 0x55; $mbr[511] = 0xAA

$entries = New-Object byte[] 512
$typeGuid = [Guid]::Parse("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")
$uniqueGuid = [Guid]::NewGuid()
[Array]::Copy($typeGuid.ToByteArray(), 0, $entries, 0, 16)
[Array]::Copy($uniqueGuid.ToByteArray(), 0, $entries, 16, 16)
Set-U64 $entries 32 34
Set-U64 $entries 40 40
$nameBytes = [Text.Encoding]::Unicode.GetBytes("DiskRescue Test Data")
[Array]::Copy($nameBytes, 0, $entries, 56, [Math]::Min(72, $nameBytes.Length))
$entriesCrc = Get-Crc32 $entries

$gpt = New-Object byte[] $imageBytes
$gpt[450] = 0xEE
Set-U32 $gpt 454 1
Set-U32 $gpt 458 ([UInt32]::MaxValue)
$gpt[510] = 0x55; $gpt[511] = 0xAA
[Array]::Copy($entries, 0, $gpt, 1024, $entries.Length)
[Array]::Copy($entries, 0, $gpt, 98 * $sectorSize, $entries.Length)
$diskGuid = [Guid]::NewGuid()
function Write-GptHeader([byte[]]$Image, [UInt64]$CurrentLba, [UInt64]$BackupLba, [UInt64]$EntriesLba) {
    $offset = [int]($CurrentLba * $sectorSize)
    $header = New-Object byte[] $sectorSize
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes("EFI PART"), 0, $header, 0, 8)
    Set-U32 $header 8 0x00010000
    Set-U32 $header 12 92
    Set-U64 $header 24 $CurrentLba
    Set-U64 $header 32 $BackupLba
    Set-U64 $header 40 34
    Set-U64 $header 48 66
    [Array]::Copy($diskGuid.ToByteArray(), 0, $header, 56, 16)
    Set-U64 $header 72 $EntriesLba
    Set-U32 $header 80 4
    Set-U32 $header 84 128
    Set-U32 $header 88 $entriesCrc
    $crcInput = New-Object byte[] 92
    [Array]::Copy($header, 0, $crcInput, 0, 92)
    Set-U32 $header 16 (Get-Crc32 $crcInput)
    [Array]::Copy($header, 0, $Image, $offset, $sectorSize)
}
Write-GptHeader $gpt 1 99 2
Write-GptHeader $gpt 99 1 98

$damagedPrimary = [byte[]]$gpt.Clone()
$damagedPrimary[528] = $damagedPrimary[528] -bxor 0x5A

$results = @()
$results += Save-NewImage "mbr-valid.img" $mbr
$results += Save-NewImage "gpt-valid.img" $gpt
$results += Save-NewImage "gpt-primary-header-damaged.img" $damagedPrimary
Write-Host ""
Write-Host "Created disposable DiskRescue Pro test images:"
$results | Format-Table -AutoSize
Write-Host "These are image files only; no physical disk was accessed."

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$fullPath = [IO.Path]::GetFullPath($OutputPath)
$directory = [IO.Path]::GetDirectoryName($fullPath)
if (-not [IO.Directory]::Exists($directory)) {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
if ([IO.File]::Exists($fullPath)) {
    throw "Refusing to overwrite an existing test image: $fullPath"
}

$tempImage = Join-Path ([IO.Path]::GetTempPath()) ("diskrescue-source-" + [Guid]::NewGuid().ToString("N") + ".img")
try {
    & (Join-Path $PSScriptRoot "New-NtfsMftPreviewImage.ps1") -OutputPath $tempImage

    $source = [IO.File]::ReadAllBytes($tempImage)
    $sectorSize = 512
    $partitionOffset = 1MB
    $partitionSourceOffset = $sectorSize
    $partitionLength = $source.Length - $partitionSourceOffset
    $image = New-Object byte[] ($partitionOffset + $partitionLength + 4096)

    # Valid empty MBR: the NTFS volume exists in the image but is absent from its partition table.
    $image[510] = 0x55
    $image[511] = 0xAA

    [Array]::Copy($source, $partitionSourceOffset, $image, $partitionOffset, $partitionLength)

    # The backup boot sector is a copy of the validated primary NTFS boot sector.
    [Array]::Copy($image, $partitionOffset, $image, $partitionOffset + $partitionLength - $sectorSize, $sectorSize)

    [IO.File]::WriteAllBytes($fullPath, $image)
    $hash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
    Write-Output "Created lost-NTFS-partition test image: $fullPath"
    Write-Output "Image length: $($image.Length) bytes"
    Write-Output "Unlisted NTFS volume offset: $partitionOffset bytes"
    Write-Output "Unlisted NTFS volume length: $partitionLength bytes"
    Write-Output "SHA256: $hash"
    Write-Output "This is a disposable synthetic fixture; it does not represent a real disk."
}
finally {
    if ([IO.File]::Exists($tempImage)) { [IO.File]::Delete($tempImage) }
}

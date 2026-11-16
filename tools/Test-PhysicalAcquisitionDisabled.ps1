$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "..\src\DiskRescue.Cli\DiskRescue.Cli.csproj"
$project = [System.IO.Path]::GetFullPath($project)

$cases = @(
    @{ Name = "list-physical-disks"; Arguments = @("list-physical-disks") },
    @{ Name = "acquire-physical"; Arguments = @("acquire-physical", "0", "NOT-A-REAL-SERIAL", (Join-Path $env:RUNNER_TEMP "must-not-be-created.img")) }
)

foreach ($case in $cases) {
    $output = & dotnet run --project $project --configuration Release --no-build -- @($case.Arguments) 2>&1
    $exitCode = $LASTEXITCODE
    $text = $output -join [Environment]::NewLine

    if ($exitCode -eq 0) {
        throw "$($case.Name) unexpectedly succeeded while physical-device access is disabled."
    }

    if ($text -notmatch "disabled" -or $text -notmatch "No physical-disk handles or device metadata were opened") {
        throw "$($case.Name) did not return the explicit pre-I/O disabled message. Output: $text"
    }

    if ($text -match "PhysicalDrive\d|SOURCE DEVICE|Enumerating physical disks") {
        throw "$($case.Name) appears to have entered physical-device inventory or acquisition. Output: $text"
    }

    Write-Output "PASS: $($case.Name) rejected before physical-device inventory or handle access."
}

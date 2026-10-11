#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes the app and builds the installer into .\artifacts.
#>
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'src\Av1Encoder\Av1Encoder.csproj'
$publishDir = Join-Path $root 'artifacts\publish'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 7 not found. Install it with: winget install JRSoftware.InnoSetup.7'
}

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

Write-Host "Publishing AV1 Encoder $version"
dotnet publish $project -c Release -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host 'Building installer'
& $iscc --quiet-progress "--define=AppVersion=$version" "--define=PublishDir=$publishDir" (Join-Path $root 'installer\Av1Encoder.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }

Write-Host "Done: $(Join-Path $root "artifacts\AV1-Encoder-Setup-$version.exe")"

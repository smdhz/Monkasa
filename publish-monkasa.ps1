param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('osx-arm64', 'osx-x64', 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'

$ProjectDir = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir 'Monkasa.csproj'
$OutputDir = Join-Path $ProjectDir "artifacts/$RuntimeIdentifier"

& dotnet publish $ProjectFile `
    -c Release `
    -r $RuntimeIdentifier `
    --self-contained true `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Published self-contained $RuntimeIdentifier package to:"
Write-Host "  $OutputDir"

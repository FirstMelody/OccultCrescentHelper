param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Destination = (Join-Path $env:APPDATA "XIVLauncherCN\devPlugins\BOCCHI"),

    [switch]$Build,

    [string]$DalamudLibPath
)

$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot "BOCCHI\BOCCHI.csproj"
$outputPath = Join-Path $repositoryRoot "BOCCHI\bin\$Configuration"

if ($Build) {
    $buildArguments = @("build", $projectPath, "-c", $Configuration)
    if ($DalamudLibPath) {
        $resolvedDalamudPath = [System.IO.Path]::GetFullPath($DalamudLibPath)
        $buildArguments += "/p:DalamudLibPath=$resolvedDalamudPath\"
    }

    dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) {
        throw "BOCCHI build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path (Join-Path $outputPath "BOCCHI.dll"))) {
    throw "Build output was not found at $outputPath."
}

$resolvedDestination = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $resolvedDestination -Force | Out-Null

Get-ChildItem -LiteralPath $outputPath -Force |
    Where-Object { $_.Name -ne "BOCCHI" } |
    Copy-Item -Destination $resolvedDestination -Recurse -Force

$deployedDll = Join-Path $resolvedDestination "BOCCHI.dll"
Write-Host "BOCCHI deployed locally:"
Write-Host $deployedDll

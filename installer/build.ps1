<#
.SYNOPSIS
    Publishes Yinyue and packages it into an MSI.

.DESCRIPTION
    Run from anywhere; paths are resolved against this script.

        .\installer\build.ps1                 # builds installer\out\Yinyue-0.1.0.msi
        .\installer\build.ps1 -Version 0.2.0

    Needs the WiX command-line tool, pinned to v5:

        dotnet tool install --global wix --version "5.*"
        wix extension add -g WixToolset.UI.wixext/5.0.2
        wix extension add -g WixToolset.Util.wixext/5.0.2

    v5 deliberately, not v6 or later: those require accepting the Open Source Maintenance Fee
    agreement before the tool will run at all. v5 is the last MS-RL release.
#>
[CmdletBinding()]
param(
    [string] $Version = "0.1.0",
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $PSScriptRoot "obj\publish"
$outDir = Join-Path $PSScriptRoot "out"
$msi = Join-Path $outDir "Yinyue-$Version.msi"

Write-Host "Publishing $Configuration build..." -ForegroundColor Cyan

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# --no-self-contained, NOT --self-contained false: the .NET 10 SDK silently ignores the
# latter and produces a 172 MB self-contained bundle instead of a 26 MB framework-dependent
# one. Measured, not assumed.
dotnet publish (Join-Path $repoRoot "win\Yinyue.csproj") `
    -c $Configuration `
    -r win-x64 `
    --no-self-contained `
    -p:PublishSingleFile=true `
    -o $publishDir `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# Debug symbols are useful next to a crash, not inside a 6 MB download.
Get-ChildItem $publishDir -Filter *.pdb | Remove-Item -Force

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "Packaging $msi..." -ForegroundColor Cyan

wix build (Join-Path $PSScriptRoot "Yinyue.wxs") `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -d Version=$Version `
    -d PublishDir=$publishDir `
    -d RepoRoot=$repoRoot `
    -o $msi

if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "Built $msi ($size MB)" -ForegroundColor Green
Write-Host ""
Write-Host "Install silently with:" -ForegroundColor DarkGray
Write-Host "  msiexec /i `"$msi`" /qn OVERLAYANCHOR=BottomRight STARTWITHWINDOWS=1" -ForegroundColor DarkGray

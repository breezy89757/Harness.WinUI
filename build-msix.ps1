<#
.SYNOPSIS
    Builds Harness.WinUI in Release and packages it as an MSIX bundle for the Microsoft Store.
.DESCRIPTION
    Builds each platform, stages the output (no .pdb files), stamps the csproj <Version> into a copy of
    src\Harness.WinUI\Package.appxmanifest, and packs everything into release\Harness.WinUI_<version>.msixbundle
    with `winapp package`. The Store signs what you upload; pass -Cert only to sideload-test locally.
.PARAMETER Platforms
    Platforms to build: x64 and/or ARM64 (default: both).
.PARAMETER Cert
    Optional .pfx to sign the bundle with (for local installation tests).
.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -Platforms x64 -Cert .\devcert.pfx
#>
param(
    [ValidateSet("x64", "ARM64")]
    [string[]]$Platforms = @("x64", "ARM64"),
    [string]$Cert
)

$ErrorActionPreference = "Stop"

$project = Join-Path $PSScriptRoot "src\Harness.WinUI\Harness.WinUI.csproj"
$manifestSource = Join-Path $PSScriptRoot "src\Harness.WinUI\Package.appxmanifest"
[xml]$proj = Get-Content $project
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
$tfm = ($proj.Project.PropertyGroup | ForEach-Object { $_.TargetFramework } | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "No <Version> in $project." }
$packageVersion = "$version.0"
Write-Host "Packaging Harness.WinUI $packageVersion for $($Platforms -join ', ') ..." -ForegroundColor Cyan

$releaseDir = Join-Path $PSScriptRoot "release"
$stagingRoot = Join-Path $releaseDir "staging"
if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }
New-Item $stagingRoot -ItemType Directory -Force | Out-Null

# Manifest with the csproj version (Identity/Version must be four parts).
[xml]$manifest = Get-Content $manifestSource
$manifest.Package.Identity.Version = $packageVersion
$stagedManifest = Join-Path $stagingRoot "Package.appxmanifest"
$manifest.Save($stagedManifest)

$layouts = @()
foreach ($platform in $Platforms) {
    Write-Host "`nBuilding $platform ..." -ForegroundColor Cyan
    # No debug info: it would embed this machine's source paths in the shipped DLLs.
    & dotnet build $project -c Release "-p:Platform=$platform" "-p:RuntimeIdentifier=win-$($platform.ToLower())" `
        -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $platform." }

    $output = Join-Path $PSScriptRoot "src\Harness.WinUI\bin\$platform\Release\$tfm\win-$($platform.ToLower())"
    if (-not (Test-Path $output)) { throw "Build output not found: $output" }

    # Stage: everything the app needs at runtime, minus debug symbols and leftovers from local runs.
    $layout = Join-Path $stagingRoot $platform
    New-Item $layout -ItemType Directory -Force | Out-Null
    Get-ChildItem $output -File | Where-Object { $_.Extension -ne ".pdb" } | Copy-Item -Destination $layout
    Get-ChildItem $output -Directory | Where-Object { $_.Name -notin @("AppX", "Harness.WinUI.exe.WebView2") } |
        Copy-Item -Destination $layout -Recurse
    Get-ChildItem $layout -Recurse -Filter *.pdb | Remove-Item -Force

    # Developer-only config must never ship (it may hold a real endpoint).
    if (Get-ChildItem $layout -Recurse -Filter "appsettings.*.json" | Where-Object Name -ne "appsettings.json") {
        throw "A developer config overlay (appsettings.*.json) ended up in the $platform layout."
    }

    # A packaged app looks its XAML (App.xbf, MainWindow.xbf, WinUI theme resources) up in resources.pri,
    # while the unpackaged build names that index after the exe. Ship it as resources.pri and tell winapp
    # not to generate its own: winapp's only indexes the tile images, and the app then fails to start.
    $appPri = Join-Path $layout "Harness.WinUI.pri"
    if (-not (Test-Path $appPri)) { throw "Harness.WinUI.pri not found in the $platform layout." }
    Move-Item $appPri (Join-Path $layout "resources.pri") -Force

    $layouts += $layout
}

$bundle = Join-Path $releaseDir "Harness.WinUI_$packageVersion.msixbundle"
if (Test-Path $bundle) { Remove-Item $bundle -Force }
$packArgs = @("package") + $layouts + @("--manifest", $stagedManifest, "--output", $bundle, "--skip-pri")
if ($Cert) { $packArgs += @("--cert", $Cert) }
Write-Host "`nPackaging ..." -ForegroundColor Cyan
& winapp @packArgs
if ($LASTEXITCODE -ne 0) { throw "winapp package failed." }

$size = [math]::Round((Get-Item $bundle).Length / 1MB, 1)
Write-Host @"

Done: release\$(Split-Path $bundle -Leaf) ($size MB)

Next steps:
  1. Make sure Identity Name / Publisher in src\Harness.WinUI\Package.appxmanifest match Partner Center.
  2. Upload the .msixbundle in Partner Center (Apps and games > Harness.WinUI > Packages).
"@ -ForegroundColor Green

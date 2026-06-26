<#
.SYNOPSIS
Publishes Cue as a self-contained, unpackaged WinUI 3 app and builds a per-user Inno Setup installer.

.DESCRIPTION
One command to produce dist\CueSetup-win-x64.exe:

  pwsh -File build-installer.ps1

Steps:
  1. Reads the version from Cue.csproj <Version> (the single source of truth; the release tag must match).
  2. Publishes self-contained + WindowsAppSDKSelfContained, unpackaged (WindowsPackageType=None) — so the
     output carries the .NET runtime and the Windows App SDK with it and needs neither installed on the
     target machine.
  3. Copies the Visual C++ 2015-2022 runtime DLLs into the publish folder (app-local). WinUI 3 depends on
     them and they are NOT part of the publish output, so bundling them keeps the install admin-free and
     self-sufficient (no separate VC++ Redistributable required).
  4. Compiles installer\Cue.iss with ISCC.

Run it from a machine with the .NET 10 SDK, the C++ runtime files (a VS C++ workload or the VC++
Redistributable), and Inno Setup 6.3+ — the same toolset the release workflow installs on its runner.
#>
param(
    [string]$Arch = 'x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-ISCC {
    $cmd = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { return $p }
    }
    throw "Inno Setup 6 (ISCC.exe) not found. Install it: choco install innosetup --no-progress -y"
}

# Bundles the VC++ runtime DLLs app-local into $Destination. WinUI 3 links against the v14.x CRT
# (vcruntime140.dll / msvcp140.dll …), which the self-contained publish does not carry. Prefers the
# VS-shipped redist (present on CI runners and dev boxes with the C++ workload), then falls back to the
# system copy that the standalone VC++ Redistributable installs.
function Copy-VCRuntime {
    param([Parameter(Mandatory)] [string]$Destination, [string]$Arch = 'x64')

    $patterns = @()
    if ($env:VCToolsRedistDir) { $patterns += Join-Path $env:VCToolsRedistDir "$Arch\Microsoft.VC*.CRT" }
    $patterns += "${env:ProgramFiles}\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\$Arch\Microsoft.VC*.CRT"
    $patterns += "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\VC\Redist\MSVC\*\$Arch\Microsoft.VC*.CRT"

    $crtDir = $null
    foreach ($pattern in $patterns) {
        $hit = Get-ChildItem -Path $pattern -Directory -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { $crtDir = $hit.FullName; break }
    }

    if ($crtDir) {
        Write-Host "  VC++ runtime: $crtDir" -ForegroundColor DarkGray
        Copy-Item (Join-Path $crtDir '*.dll') -Destination $Destination -Force
    }
    else {
        # Fallback: the named DLLs from System32 (present when the standalone VC++ Redistributable is installed).
        Write-Host "  VC++ runtime: VS redist not found — falling back to System32" -ForegroundColor DarkGray
        $named = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll',
            'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'vccorlib140.dll', 'concrt140.dll')
        foreach ($dll in $named) {
            $src = Join-Path $env:WINDIR "System32\$dll"
            if (Test-Path $src) { Copy-Item $src -Destination $Destination -Force }
        }
    }

    # Fail the build (rather than ship a non-launching installer) if the essentials didn't land.
    foreach ($dll in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
        if (-not (Test-Path (Join-Path $Destination $dll))) {
            throw "VC++ runtime '$dll' was not bundled. Install the VS C++ workload or the 'Microsoft Visual C++ Redistributable'."
        }
    }
}

# --- 1. Version (single source of truth: Cue.csproj <Version>) ---
[xml]$proj = Get-Content (Join-Path $root 'Cue.csproj')
$version = @($proj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "Cue.csproj has no <Version> element." }
Write-Host "Building Cue $version ($Arch)" -ForegroundColor Cyan

$rid = "win-$Arch"
$publishDir = Join-Path $root "bin/Release/net10.0-windows10.0.26100.0/$rid/publish"

# --- 2. Publish: self-contained, WinAppSDK self-contained, unpackaged ---
Write-Host "Publishing…" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& dotnet publish (Join-Path $root 'Cue.csproj') -c Release -r $rid -p:Platform=$Arch `
    -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false -p:PublishReadyToRun=true --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# --- 2b. Guard: the shipped publish must carry no Windows AI/ML payload ---
# Cue references the Windows App SDK by component package (.WinUI + .DWrite in Cue.csproj) precisely
# to keep the ~44 MB onnxruntime.dll / DirectML.dll / Microsoft.Windows.AI.* stack out of the app.
# This is the publish-side counterpart to the GuardAgainstAIMLArtifacts MSBuild target: that one
# checks the build's dependency closure, this one checks the actual shipped folder. If a future
# dependency re-introduces Microsoft.WindowsAppSDK.ML/.AI, the installer build fails here rather than
# silently regaining ~44 MB.
$aiml = Get-ChildItem -LiteralPath $publishDir -Recurse -File | Where-Object {
    $_.Name -match '^(onnxruntime|DirectML)\.dll$' -or
    $_.Name -like 'Microsoft.Windows.AI.*' -or
    $_.Name -like 'Microsoft.ML.OnnxRuntime*' -or
    $_.Name -like 'Microsoft.Graphics.Imaging*'
}
if ($aiml) {
    throw "Windows AI/ML artifacts in publish output: $(($aiml | ForEach-Object Name) -join ', '). A dependency re-introduced Microsoft.WindowsAppSDK.ML/.AI — see the PackageReference comment in Cue.csproj."
}

# --- 2c. Trim Windows App SDK per-locale resources to the languages Cue ships ---
# The publish carries ~80 per-locale MUI folders (Microsoft.ui.xaml.dll.mui et al.), one per Windows
# display language. Cue is Korean-only (English the one planned addition), so the rest are dead
# weight. SatelliteResourceLanguages (Cue.csproj) prunes .NET satellite assemblies but NOT these
# WinAppSDK MUI folders, so they're removed here on the shipped output. The allowlist keeps Korean
# and both English variants.
Write-Host "Trimming locale resources..." -ForegroundColor Cyan
$keepLocales = @('ko-KR', 'en-us', 'en-GB')
$removed = 0; $freed = 0
Get-ChildItem -LiteralPath $publishDir -Directory |
    Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z0-9]+)+$' -and $keepLocales -notcontains $_.Name } |
    ForEach-Object {
        $freed += (Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum
        Remove-Item $_.FullName -Recurse -Force
        $removed++
    }
Write-Host ("  removed {0} locale folders ({1:N1} MB)" -f $removed, ($freed / 1MB)) -ForegroundColor DarkGray

# --- 3. Bundle the VC++ runtime app-local ---
Write-Host "Bundling VC++ runtime…" -ForegroundColor Cyan
Copy-VCRuntime -Destination $publishDir -Arch $Arch

# --- 4. Compile the installer ---
Write-Host "Compiling installer…" -ForegroundColor Cyan
$iscc = Get-ISCC
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
& $iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" (Join-Path $root 'installer/Cue.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

# --- 5. Size regression guard ---
# Ceilings, not exact targets: they catch a large regression (the ~44 MB AI/ML stack returning, or
# the per-locale resources creeping back) while leaving headroom for normal growth. Measured
# baseline as of v0.1.2 — component-package WinUI build, AI/ML excluded, locales trimmed: publish
# ~237 MB (was ~291 MB on the metapackage). The installer (LZMA2 solid) is roughly half that; its
# ceiling is set loosely until the first CI run pins the real number. Bump these deliberately when a
# real, reviewed size increase lands. (The AI/ML stack returning, ~44 MB, trips $maxPublishMB and
# the explicit guards above well before either installer ceiling.)
$maxPublishMB = 270
$maxInstallerMB = 110
$installer = Join-Path $dist 'CueSetup-win-x64.exe'
$publishMB = (Get-ChildItem -LiteralPath $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
$installerMB = (Get-Item -LiteralPath $installer).Length / 1MB
Write-Host ("Sizes — publish {0:N1} MB (ceiling {1}), installer {2:N1} MB (ceiling {3})" -f `
        $publishMB, $maxPublishMB, $installerMB, $maxInstallerMB) -ForegroundColor Cyan
$regressions = @()
if ($publishMB -gt $maxPublishMB) { $regressions += ("publish folder {0:N1} MB exceeds {1} MB ceiling" -f $publishMB, $maxPublishMB) }
if ($installerMB -gt $maxInstallerMB) { $regressions += ("installer {0:N1} MB exceeds {1} MB ceiling" -f $installerMB, $maxInstallerMB) }
if ($regressions) {
    throw "Size regression — $($regressions -join '; '). Investigate, or raise the ceiling in build-installer.ps1 if the increase is intended."
}

Write-Host "Done → $installer" -ForegroundColor Green

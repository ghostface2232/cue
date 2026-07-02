<#
.SYNOPSIS
Builds Cue's unsigned x64 MSIX for Microsoft Partner Center.

.DESCRIPTION
Produces dist\Cue_<manifest-version>_x64.msix. The package is deliberately unsigned because the
Microsoft Store signs accepted submissions. Cue remains fully self-contained: both the .NET runtime
and Windows App SDK binaries are inside the MSIX. The unpackaged-only WindowsPackageType=None setting
is not used, and the bootstrapper/UndockedRegFreeWinRT auto-initializers are explicitly disabled for
this packaged self-contained build.

The generated MSIX declares Microsoft.VCLibs.140.00.UWPDesktop instead of copying VC++ runtime DLLs
app-local. Partner Center/Store installs that framework dependency with the app.
#>

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $root 'Cue.csproj'
$manifestPath = Join-Path $root 'Package.appxmanifest'
$distDir = Join-Path $root 'dist'
$stagingDir = Join-Path $distDir 'msix-staging'
$workDir = Join-Path $root 'obj\msix-store'
$unpackDir = Join-Path $workDir 'unpacked'
$verifyDir = Join-Path $workDir 'verify'
$arch = 'x64'
$rid = 'win-x64'

function Get-ProjectVersion {
    [xml]$project = Get-Content -LiteralPath $projectPath
    $value = @($project.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if (-not $value) { throw 'Cue.csproj has no <Version> element.' }

    $parsed = $null
    if (-not [Version]::TryParse([string]$value, [ref]$parsed) -or $parsed.Revision -ge 0) {
        throw "Cue.csproj <Version> '$value' must be a three-part version such as 0.1.4."
    }
    return [string]$value
}

function Assert-ManifestInputs {
    param([Parameter(Mandatory)] [string]$ProjectVersion)

    [xml]$manifest = Get-Content -LiteralPath $manifestPath
    $manifestVersion = [string]$manifest.Package.Identity.Version
    $expectedVersion = "$ProjectVersion.0"
    if ($manifestVersion -ne $expectedVersion) {
        throw "Package.appxmanifest Identity/Version '$manifestVersion' must be '$expectedVersion' (Cue.csproj <Version> + '.0')."
    }

    $text = Get-Content -LiteralPath $manifestPath -Raw
    $assetRefs = [regex]::Matches($text, 'Assets\\[^"<>]+?\.(?:png|ico|jpg|jpeg)') |
        ForEach-Object { $_.Value } | Sort-Object -Unique
    $missing = @()
    foreach ($ref in $assetRefs) {
        $relativeDir = Split-Path $ref -Parent
        $assetDir = Join-Path $root $relativeDir
        $baseName = [IO.Path]::GetFileNameWithoutExtension($ref)
        $extension = [IO.Path]::GetExtension($ref)
        $exactPath = Join-Path $root $ref
        $variants = Get-ChildItem -LiteralPath $assetDir -Filter "$baseName*$extension" -File -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $exactPath -PathType Leaf) -and -not $variants) {
            $missing += $ref
        }
    }
    if ($missing) {
        throw "Package.appxmanifest references assets with no matching file: $($missing -join ', ')"
    }

    $dependency = @($manifest.Package.Dependencies.PackageDependency | Where-Object {
        $_.Name -eq 'Microsoft.VCLibs.140.00.UWPDesktop'
    })
    if ($dependency.Count -ne 1) {
        throw 'Package.appxmanifest must declare exactly one Microsoft.VCLibs.140.00.UWPDesktop dependency.'
    }

    Write-Host "Validated manifest version $manifestVersion and $($assetRefs.Count) asset references." -ForegroundColor DarkGray
}

function Get-MakeAppx {
    $command = Get-Command 'makeappx.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    [xml]$project = Get-Content -LiteralPath $projectPath
    $buildToolsVersion = @($project.Project.ItemGroup.PackageReference |
        Where-Object { $_.Include -eq 'Microsoft.Windows.SDK.BuildTools' } |
        Select-Object -ExpandProperty Version -First 1)
    if ($buildToolsVersion) {
        $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
        $packageRoot = Join-Path $nugetRoot "microsoft.windows.sdk.buildtools\$buildToolsVersion\bin"
        $candidate = Get-ChildItem -LiteralPath $packageRoot -Recurse -Filter 'makeappx.exe' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $kitCandidate = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter 'makeappx.exe' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'x64' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($kitCandidate) { return $kitCandidate.FullName }

    throw 'makeappx.exe was not found. Restore Microsoft.Windows.SDK.BuildTools or install the Windows SDK.'
}

function Invoke-MakeAppx {
    param(
        [Parameter(Mandatory)] [string]$Tool,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $output = @(& $Tool @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        $output | Out-Host
        throw "makeappx.exe failed ($LASTEXITCODE)."
    }
}

function Remove-UnshippedLocales {
    param([Parameter(Mandatory)] [string]$PackageRoot)

    # Keep the Korean UI plus the English fallback resources, matching build-installer.ps1.
    $keepLocales = @('ko-KR', 'en-us', 'en-GB')
    $removed = 0
    $freed = 0L
    Get-ChildItem -LiteralPath $PackageRoot -Directory |
        Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z0-9]+)+$' -and $keepLocales -notcontains $_.Name } |
        ForEach-Object {
            $size = (Get-ChildItem -LiteralPath $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum
            if ($size) { $freed += $size }
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
            $removed++
        }
    Write-Host ("Trimmed {0} WinAppSDK locale folders ({1:N1} MB)." -f $removed, ($freed / 1MB)) -ForegroundColor DarkGray
}

function Assert-FinalPackage {
    param(
        [Parameter(Mandatory)] [string]$PackageRoot,
        [Parameter(Mandatory)] [string]$ExpectedVersion
    )

    [xml]$manifest = Get-Content -LiteralPath (Join-Path $PackageRoot 'AppxManifest.xml')
    if ($manifest.Package.Identity.Version -ne $ExpectedVersion) {
        throw "Final MSIX version '$($manifest.Package.Identity.Version)' does not match '$ExpectedVersion'."
    }
    if ($manifest.Package.Identity.ProcessorArchitecture -ne 'x64') {
        throw "Final MSIX architecture '$($manifest.Package.Identity.ProcessorArchitecture)' is not x64."
    }

    $dependencies = @($manifest.Package.Dependencies.PackageDependency)
    if (-not ($dependencies | Where-Object { $_.Name -eq 'Microsoft.VCLibs.140.00.UWPDesktop' })) {
        throw 'Final MSIX does not declare Microsoft.VCLibs.140.00.UWPDesktop.'
    }
    if ($dependencies | Where-Object { $_.Name -like 'Microsoft.WindowsAppRuntime.*' }) {
        throw 'Final MSIX unexpectedly depends on a Windows App Runtime framework package; it must remain self-contained.'
    }

    foreach ($file in @('Cue.exe', 'coreclr.dll', 'Microsoft.ui.xaml.dll', 'DWriteCore.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageRoot $file) -PathType Leaf)) {
            throw "Final MSIX is missing self-contained payload file '$file'."
        }
    }
    if (Get-ChildItem -LiteralPath $PackageRoot -File | Where-Object { $_.Name -match '^(?:vcruntime|msvcp)140.*\.dll$' }) {
        throw 'Final MSIX contains app-local VC++ runtime DLLs; use the declared VCLibs framework dependency instead.'
    }
    if (Test-Path -LiteralPath (Join-Path $PackageRoot 'AppxSignature.p7x')) {
        throw 'Final MSIX is signed. Partner Center input must remain unsigned.'
    }

    $unexpectedLocales = Get-ChildItem -LiteralPath $PackageRoot -Directory |
        Where-Object { $_.Name -match '^[a-z]{2}(-[A-Za-z0-9]+)+$' -and @('ko-KR', 'en-us', 'en-GB') -notcontains $_.Name }
    if ($unexpectedLocales) {
        throw "Final MSIX contains unshipped locale folders: $(($unexpectedLocales.Name) -join ', ')"
    }

    $aiml = Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | Where-Object {
        $_.Name -match '^(onnxruntime|DirectML)\.dll$' -or
        $_.Name -like 'Microsoft.Windows.AI.*' -or
        $_.Name -like 'Microsoft.ML.OnnxRuntime*' -or
        $_.Name -like 'Microsoft.Graphics.Imaging*'
    }
    if ($aiml) {
        throw "Windows AI/ML artifacts entered the MSIX: $(($aiml.Name) -join ', ')"
    }
}

$version = Get-ProjectVersion
$manifestVersion = "$version.0"
Assert-ManifestInputs -ProjectVersion $version
Write-Host "Building unsigned Cue $manifestVersion Store MSIX ($arch)..." -ForegroundColor Cyan

foreach ($path in @($stagingDir, $workDir)) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $distDir, $stagingDir, $workDir | Out-Null

# SideloadOnly asks the single-project pipeline for the raw MSIX rather than an upload container.
# Partner Center accepts this unsigned MSIX and signs it after certification. CI mode would also try
# to generate a symbols/upload container and requires Visual Studio-only PDB conversion tooling.
$publishArgs = @(
    'publish', $projectPath,
    '-c', 'Release',
    '-r', $rid,
    '-p:Platform=x64',
    '-p:WindowsPackageType=MSIX',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:WindowsAppSdkBootstrapInitialize=false',
    '-p:WindowsAppSdkUndockedRegFreeWinRTInitialize=false',
    '-p:GenerateAppxPackageOnBuild=true',
    '-p:UapAppxPackageBuildMode=SideloadOnly',
    '-p:AppxBundle=Never',
    '-p:AppxPackageSigningEnabled=false',
    '-p:PackageCertificateThumbprint=',
    '-p:AppxSymbolPackageEnabled=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    "-p:AppxPackageDir=$stagingDir$([IO.Path]::DirectorySeparatorChar)",
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=true',
    '--nologo'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

$sourcePackages = @(Get-ChildItem -LiteralPath $stagingDir -Recurse -Filter '*.msix' -File)
if ($sourcePackages.Count -ne 1) {
    throw "Expected exactly one x64 MSIX in '$stagingDir', found $($sourcePackages.Count)."
}

$makeAppx = Get-MakeAppx
Write-Host 'Trimming package locale resources...' -ForegroundColor Cyan
Invoke-MakeAppx -Tool $makeAppx -Arguments @('unpack', '/p', $sourcePackages[0].FullName, '/d', $unpackDir, '/o')
Remove-UnshippedLocales -PackageRoot $unpackDir

# makeappx regenerates the block map while packing; the old one is reserved input metadata.
Remove-Item -LiteralPath (Join-Path $unpackDir 'AppxBlockMap.xml') -Force
$finalPackage = Join-Path $distDir "Cue_${manifestVersion}_x64.msix"
if (Test-Path -LiteralPath $finalPackage) { Remove-Item -LiteralPath $finalPackage -Force }
Invoke-MakeAppx -Tool $makeAppx -Arguments @('pack', '/d', $unpackDir, '/p', $finalPackage, '/o')

Write-Host 'Verifying final package...' -ForegroundColor Cyan
Invoke-MakeAppx -Tool $makeAppx -Arguments @('unpack', '/p', $finalPackage, '/d', $verifyDir, '/o')
Assert-FinalPackage -PackageRoot $verifyDir -ExpectedVersion $manifestVersion

$sizeMB = (Get-Item -LiteralPath $finalPackage).Length / 1MB
Remove-Item -LiteralPath $stagingDir, $workDir -Recurse -Force
Write-Host ("Done -> {0} ({1:N1} MB, unsigned)" -f $finalPackage, $sizeMB) -ForegroundColor Green

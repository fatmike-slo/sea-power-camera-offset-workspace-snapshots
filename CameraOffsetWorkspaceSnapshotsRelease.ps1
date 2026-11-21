# CameraOffsetWorkspaceSnapshots — guarded v1.0.0 Anchor Chain local release controller.
# Windows PowerShell 5.1; no Steam upload, no changes to other installed mods.
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Build', 'Package', 'Install', 'Verify', 'Rollback')]
    [string]$Action = 'Check',
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power',
    [string]$AnchorChainDll = ''
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$testRoot = Join-Path $root '_camera-workspace-v1-release'
$buildSource = Join-Path $testRoot 'build\src'
$outputMod = Join-Path $testRoot 'CameraOffsetWorkspaceSnapshots'
$outputDll = Join-Path $outputMod 'CustomCameraOffset.dll'
$sourceDll = Join-Path $buildSource 'bin\Release\netstandard2.1\CustomCameraOffset.dll'
$manualDll = Join-Path $GameDir 'BepInEx\plugins\CustomCameraOffset\CustomCameraOffset.dll'
$pluginRoot = Join-Path $GameDir 'BepInEx\plugins'
$streamingAssets = Join-Path $GameDir 'Sea Power_Data\StreamingAssets'
$installedMod = Join-Path $streamingAssets 'CameraOffsetWorkspaceSnapshots'
$installedDll = Join-Path $installedMod 'CustomCameraOffset.dll'
$installedIni = Join-Path $installedMod '_info.ini'
$markerFile = Join-Path $installedMod '.camera-workspace-v1-local-install'
$markerText = 'CameraOffsetWorkspaceSnapshots-Workshop-v1.0.0-local-install'
$stateFile = Join-Path $testRoot 'test-state.json'

function Assert-GameClosed {
    if (Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue) {
        throw 'Sea Power is currently running. Fully close the game before changing local mod files.'
    }
}
function Get-Hash([string]$file) {
    return (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToUpperInvariant()
}
function Get-GameInfo {
    if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'Sea Power.exe') -PathType Leaf)) {
        throw "Sea Power.exe not found: $GameDir"
    }
    $managedCandidates = @(Get-ChildItem -LiteralPath $GameDir -Directory -Filter '*_Data' |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Managed') -PathType Container })
    if ($managedCandidates.Count -ne 1) {
        throw "Expected exactly one Unity Managed directory; found $($managedCandidates.Count)."
    }
    $managed = Join-Path $managedCandidates[0].FullName 'Managed'
    $refs = @('Seapower-Scripts.dll','Noesis.NoesisGUI.dll','Libraries.dll','UniRx.dll',
        'CommunityToolkit.Mvvm.dll','UnityEngine.dll','UnityEngine.CoreModule.dll',
        'UnityEngine.InputLegacyModule.dll','UnityEngine.IMGUIModule.dll')
    $missing = @($refs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $managed $_) -PathType Leaf) })
    foreach ($f in @('BepInEx.dll','0Harmony.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $GameDir "BepInEx\core\$f") -PathType Leaf)) {
            $missing += "BepInEx/core/$f"
        }
    }
    if ($missing.Count -gt 0) { throw "Missing Sea Power/BepInEx reference DLL(s): $($missing -join ', ')" }
    return [pscustomobject]@{ ManagedDir = $managed }
}
function Resolve-AnchorChainDll {
    if ($AnchorChainDll) {
        if (-not (Test-Path -LiteralPath $AnchorChainDll -PathType Leaf)) {
            throw "Specified AnchorChain.dll not found: $AnchorChainDll"
        }
        return (Resolve-Path -LiteralPath $AnchorChainDll).Path
    }
    $steamApps = Split-Path -Parent (Split-Path -Parent $GameDir)
    $locations = @(
        (Join-Path $steamApps 'workshop\content\1286220\3380210757'),
        $streamingAssets,
        $pluginRoot
    )
    $found = @()
    foreach ($dir in $locations) {
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        $found += @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter 'AnchorChain.dll' -ErrorAction Stop |
            Select-Object -ExpandProperty FullName)
    }
    $found = @($found | Select-Object -Unique)
    if ($found.Count -eq 0) {
        throw 'AnchorChain.dll not found under installed Workshop 3380210757, StreamingAssets or BepInEx/plugins. Pass -AnchorChainDll "FULL\PATH\AnchorChain.dll".'
    }
    if ($found.Count -gt 1) {
        throw "Multiple AnchorChain.dll copies found; choose one with -AnchorChainDll: $($found -join '; ')"
    }
    return $found[0]
}
function Assert-DotNet8 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '.NET SDK not found on PATH. The handoff environment uses .NET 8 SDK.'
    }
    $sdks = @(& dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0 -or -not @($sdks | Where-Object { $_ -match '^8\.' }).Count) {
        throw "Cannot find .NET 8 SDK. Installed SDK(s): $($sdks -join '; ')"
    }
    return ($sdks -join '; ')
}
function Assert-Source {
    $p = Join-Path $root 'src\Plugin.cs'
    $source = Get-Content -LiteralPath $p -Raw
    if ($source -notmatch '\[BepInPlugin\("local\.seapower\.customcameraoffset",\s*"Camera Offset & Workspace Snapshots",\s*"1\.0\.0"\)\]') {
        throw 'Expected internal BepInPlugin GUID and v1.0.0 display name/version were not found.'
    }
    $w = Get-Content -LiteralPath (Join-Path $root 'src\WorkshopEntryPoint.cs') -Raw
    if ($w -notmatch 'IAnchorChainMod' -or $w -notmatch 'ANCHORCHAIN_WORKSHOP') {
        throw 'Anchor Chain adapter source is missing.'
    }
}
function Find-CopiesOutsideExpected {
    # Detect another installed manual DLL, local mod, or subscribed Workshop copy.
    $bad = @()
    $steamApps = Split-Path -Parent (Split-Path -Parent $GameDir)
    $steamWorkshopMods = Join-Path $steamApps 'workshop\content\1286220'
    foreach ($folder in @($pluginRoot,$streamingAssets,$steamWorkshopMods)) {
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) { continue }
        $bad += @(Get-ChildItem -LiteralPath $folder -Recurse -File -Filter 'CustomCameraOffset.dll' |
            Where-Object { $_.FullName -ine $manualDll -and $_.FullName -ine $installedDll } |
            Select-Object -ExpandProperty FullName)
    }
    return @($bad | Select-Object -Unique)
}
function Write-CurrentStatus {
    $extra = @(Find-CopiesOutsideExpected)
    [pscustomobject]@{
        GameDir = $GameDir
        ManualDllPresent = (Test-Path -LiteralPath $manualDll -PathType Leaf)
        LocalWorkshopTestPresent = (Test-Path -LiteralPath $installedDll -PathType Leaf)
        TestStatePresent = (Test-Path -LiteralPath $stateFile -PathType Leaf)
        ExtraCopiesDetected = ($extra -join '; ')
    } | Format-List
}
function Run-Check {
    $game = Get-GameInfo
    Assert-Source
    $sdk = Assert-DotNet8
    $anchorDll = Resolve-AnchorChainDll
    Write-Host 'CAMERA OFFSET & WORKSPACE SNAPSHOTS v1.0.0 — read-only preflight OK' -ForegroundColor Green
    [pscustomobject]@{
        ManagedDir = $game.ManagedDir
        DotNetSDK = $sdk
        AnchorChainDll = $anchorDll
        AnchorChainFileVersion = ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($anchorDll).FileVersion)
        SeaPowerRunning = [bool](Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue)
    } | Format-List
    Write-CurrentStatus
    if ((Find-CopiesOutsideExpected).Count -gt 0) {
        Write-Warning 'Extra copies detected. Install is blocked until their location is resolved.'
    }
}
function Run-Build {
    $game = Get-GameInfo
    Assert-Source
    [void](Assert-DotNet8)
    $anchorDll = Resolve-AnchorChainDll
    if (Test-Path -LiteralPath $stateFile) {
        throw 'A local release is installed; Rollback before building another release to avoid hash/version confusion.'
    }
    if (Test-Path -LiteralPath $buildSource) {
        Remove-Item -LiteralPath $buildSource -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $buildSource,$outputMod | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root 'src') -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $buildSource -Force }
    $env:SP_CCO_GAME_DIR = $GameDir
    $env:SP_CCO_MANAGED_DIR = $game.ManagedDir
    $project = Join-Path $buildSource 'CustomCameraOffset.csproj'
    & dotnet build $project -c Release --nologo --no-incremental '-p:WorkshopBuild=true' "-p:AnchorChainDll=$anchorDll"
    if ($LASTEXITCODE -ne 0) { throw 'Workshop compilation FAILED. No game files modified; do not Install.' }
    if (-not (Test-Path -LiteralPath $sourceDll -PathType Leaf)) {
        throw 'dotnet exited successfully but did not create CustomCameraOffset.dll; do not Install.'
    }
    Copy-Item -LiteralPath $sourceDll -Destination $outputDll -Force
    Copy-Item -LiteralPath (Join-Path $root 'workshop\_info.ini') -Destination $outputMod -Force
    $builtHash = Get-Hash $outputDll
    Write-Host "BUILD OK (compile-only, NOT runtime tested): $outputDll" -ForegroundColor Green
    Write-Host "SHA256: $builtHash"
    Write-Host 'No installed game files were changed. Next: -Action Package, or -Action Install (game must be closed).'
}
function Run-Install {
    Assert-GameClosed
    [void](Get-GameInfo)
    if (-not (Test-Path -LiteralPath $outputDll -PathType Leaf)) {
        throw 'No compiled Workshop DLL found. Run -Action Build first; do not install unbuilt source.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $outputMod '_info.ini') -PathType Leaf)) {
        throw 'Built Workshop _info.ini is missing. Re-run Build.'
    }
    if (Test-Path -LiteralPath $stateFile) { throw 'Release already installed; Rollback before another Install.' }
    if (Test-Path -LiteralPath $installedMod) {
        throw "Refusing to touch an existing mod folder: $installedMod. Inspect it; only this script's new folder may be staged."
    }
    $other = @(Find-CopiesOutsideExpected)
    if ($other.Count -gt 0) { throw "Other CustomCameraOffset.dll copies exist. Resolve first: $($other -join '; ')" }
    $hadManual = Test-Path -LiteralPath $manualDll -PathType Leaf
    $manualHash = if ($hadManual) { Get-Hash $manualDll } else { '' }
    $backupPath = ''
    if ($hadManual) {
        $backups = Join-Path $testRoot 'backups'
        New-Item -Path $backups -ItemType Directory -Force | Out-Null
        $backupPath = Join-Path $backups ("CustomCameraOffset.dll.manual-" + (Get-Date -Format 'yyyyMMdd-HHmmssfff'))
    }
    $dllHash = Get-Hash $outputDll
    $iniHash = Get-Hash (Join-Path $outputMod '_info.ini')
    try {
        if ($hadManual) {
            Move-Item -LiteralPath $manualDll -Destination $backupPath -ErrorAction Stop
        }
        New-Item -ItemType Directory -Path $installedMod -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath $outputDll -Destination $installedDll -ErrorAction Stop
        Copy-Item -LiteralPath (Join-Path $outputMod '_info.ini') -Destination $installedIni -ErrorAction Stop
        Set-Content -LiteralPath $markerFile -Value $markerText -Encoding Ascii -ErrorAction Stop
        $state = [pscustomobject]@{
            Version = 'CameraOffsetWorkspaceSnapshots-v1.0.0'; GameDir = $GameDir
            HadManual = [bool]$hadManual; BackupPath = $backupPath; ManualHash = $manualHash
            StageDllHash = $dllHash; StageIniHash = $iniHash
        }
        $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $stateFile -Encoding UTF8 -ErrorAction Stop
    }
    catch {
        # Only freshly created stage files; never touch other mod directories or configuration.
        if (Test-Path -LiteralPath $markerFile) { Remove-Item -LiteralPath $markerFile -Force }
        if (Test-Path -LiteralPath $installedDll) { Remove-Item -LiteralPath $installedDll -Force }
        if (Test-Path -LiteralPath $installedIni) { Remove-Item -LiteralPath $installedIni -Force }
        if ((Test-Path -LiteralPath $installedMod) -and
            -not @(Get-ChildItem -LiteralPath $installedMod -Force).Count) {
            Remove-Item -LiteralPath $installedMod -Force
        }
        if ($hadManual -and (Test-Path -LiteralPath $backupPath) -and -not (Test-Path -LiteralPath $manualDll)) {
            Move-Item -LiteralPath $backupPath -Destination $manualDll
        }
        if (Test-Path -LiteralPath $stateFile) { Remove-Item -LiteralPath $stateFile -Force }
        throw
    }
    Write-Host 'INSTALL OK — LOCAL RELEASE; previous manual DLL backed up outside BepInEx/plugins.' -ForegroundColor Green
    Write-Host "Local release mod: $installedMod"
    if ($hadManual) { Write-Host "Stable manual DLL backup: $backupPath" }
    Write-Host 'Your BepInEx config was not modified. Check -Action Verify, then run a fresh in-game test.'
}
function Get-State {
    if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
        throw 'No local v1.0.0 installation state exists; cannot safely identify files for rollback.'
    }
    $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    if ($state.Version -ne 'CameraOffsetWorkspaceSnapshots-v1.0.0' -or $state.GameDir -ine $GameDir) {
        throw 'State mismatch: different game directory or test version. Refusing to modify game files.'
    }
    return $state
}
function Assert-StageUnmodified($state) {
    if (-not (Test-Path -LiteralPath $markerFile -PathType Leaf) -or
        (Get-Content -LiteralPath $markerFile -Raw).Trim() -ne $markerText) {
        throw 'Local release marker missing/changed. Refusing to delete an unrecognized mod directory.'
    }
    if (-not (Test-Path -LiteralPath $installedDll -PathType Leaf) -or
        (Get-Hash $installedDll) -ne $state.StageDllHash) {
        throw 'Local release DLL is missing or changed. Refusing automatic removal.'
    }
    if (-not (Test-Path -LiteralPath $installedIni -PathType Leaf) -or
        (Get-Hash $installedIni) -ne $state.StageIniHash) {
        throw 'Local Workshop _info.ini is missing or changed. Refusing automatic removal.'
    }
    $unexpected = @(Get-ChildItem -LiteralPath $installedMod -Force |
        Where-Object { $_.FullName -ine $installedDll -and $_.FullName -ine $installedIni -and $_.FullName -ine $markerFile })
    if ($unexpected.Count -ne 0) {
        throw "Local release directory contains unexpected files. Refusing removal: $($unexpected.FullName -join ', ')"
    }
}
function Run-Verify {
    if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
        Write-Host 'No v1.0.0 installation state. Existing files are untouched.'
        Write-CurrentStatus
        return
    }
    $state = Get-State
    Assert-StageUnmodified $state
    if (Test-Path -LiteralPath $manualDll) {
        throw 'DUPLICATE: the manual DLL is present while the Workshop test is staged.'
    }
    if ($state.HadManual -and ((-not (Test-Path -LiteralPath $state.BackupPath -PathType Leaf)) -or
        (Get-Hash $state.BackupPath) -ne $state.ManualHash)) {
        throw 'Original stable DLL backup is missing or changed. Stop testing and inspect the backup.'
    }
    $other = @(Find-CopiesOutsideExpected)
    if ($other.Count -gt 0) { throw "Extra mod DLLs detected: $($other -join '; ')" }
    Write-Host 'DISK VERIFY OK: exactly one staged local release DLL, manual DLL outside plugins, backup intact.' -ForegroundColor Green
    Write-Host "Local release SHA256: $($state.StageDllHash)"
    Write-Host 'This check DOES NOT prove that Anchor Chain or Ctrl+Shift+U loads in-game.'
    $log = Join-Path $GameDir 'BepInEx\LogOutput.log'
    if (Test-Path -LiteralPath $log) {
        Get-Item -LiteralPath $log | Select-Object LastWriteTime,Length | Format-List
        Write-Host 'Log timestamp above must correspond to the CURRENT game launch; otherwise matches may be stale.'
        Get-Content -LiteralPath $log -Tail 500 |
            Select-String 'CameraOffsetWorkspaceSnapshots|CustomCameraOffset|Skipping type|AnchorChain|Exception' |
            Select-Object -Last 25
    }
}
function Run-Rollback {
    Assert-GameClosed
    $state = Get-State
    Assert-StageUnmodified $state
    if (Test-Path -LiteralPath $manualDll) {
        throw 'A manual DLL already exists. Refusing to overwrite; inspect before rollback.'
    }
    if ($state.HadManual) {
        if (-not (Test-Path -LiteralPath $state.BackupPath -PathType Leaf) -or
            (Get-Hash $state.BackupPath) -ne $state.ManualHash) {
            throw 'Stable manual backup missing/changed. Staged test has NOT been removed.'
        }
    }
    Remove-Item -LiteralPath $installedDll -Force
    Remove-Item -LiteralPath $installedIni -Force
    Remove-Item -LiteralPath $markerFile -Force
    Remove-Item -LiteralPath $installedMod -Force
    if ($state.HadManual) {
        $manualParent = Split-Path -Parent $manualDll
        if (-not (Test-Path -LiteralPath $manualParent)) {
            New-Item -Path $manualParent -ItemType Directory -Force | Out-Null
        }
        Move-Item -LiteralPath $state.BackupPath -Destination $manualDll -ErrorAction Stop
        if ((Get-Hash $manualDll) -ne $state.ManualHash) {
            throw 'Restored DLL hash mismatch; inspect manually. State file intentionally retained.'
        }
    }
    Remove-Item -LiteralPath $stateFile -Force
    Write-Host 'ROLLBACK OK: local test files removed; original manual DLL restored if it existed.' -ForegroundColor Green
    Write-Host 'BepInEx configuration and all unrelated game files are unchanged.'
}


function Run-Package {
    if (-not (Test-Path -LiteralPath $outputDll -PathType Leaf)) {
        throw 'Compiled CustomCameraOffset.dll missing. Run -Action Build first.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $outputMod '_info.ini') -PathType Leaf)) {
        throw 'Workshop metadata missing. Run -Action Build again.'
    }
    $dist = Join-Path $root 'dist'
    New-Item -Path $dist -ItemType Directory -Force | Out-Null
    $archive = Join-Path $dist 'CameraOffset_WorkspaceSnapshots_v1.0.0_Workshop.zip'
    if (Test-Path -LiteralPath $archive) {
        throw "Release archive already exists: $archive. Preserve it and inspect before rebuilding."
    }
    Compress-Archive -Path $outputMod -DestinationPath $archive -CompressionLevel Optimal -ErrorAction Stop
    $dllHash = Get-Hash $outputDll
    $archiveHash = Get-Hash $archive
    Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding Ascii -Value @(
        "$dllHash  CustomCameraOffset.dll",
        "$archiveHash  CameraOffset_WorkspaceSnapshots_v1.0.0_Workshop.zip"
    )
    Write-Host "PACKAGE OK: $archive" -ForegroundColor Green
    Write-Host "DLL SHA256: $dllHash"
    Write-Host "ZIP SHA256: $archiveHash"
    Write-Host 'Contains compiled mod DLL and Workshop metadata only; no proprietary game assemblies.'
    Write-Host 'Zip is a local release candidate. Confirm Windows build, install verification, and in-game test before public release.'
}

switch ($Action) {
    'Check' { Run-Check }
    'Build' { Run-Build }
    'Package' { Run-Package }
    'Install' { Run-Install }
    'Verify' { Run-Verify }
    'Rollback' { Run-Rollback }
}

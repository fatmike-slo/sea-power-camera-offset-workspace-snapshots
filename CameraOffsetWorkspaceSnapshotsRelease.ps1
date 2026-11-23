# Guarded v1.0.1 public release controller. Windows PowerShell 5.1.
# Reuses the exact, in-game-tested RC2 DLL; no rebuild, no Steam credentials, no upload automation.
[CmdletBinding()]
param(
    [ValidateSet('Check','Package','VerifyPackage','Stage','VerifyStage','Clean','VerifyWorkshop')]
    [string]$Action = 'Check',
    [string]$RC2Dir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'sea-power-camera-offset-workspace-snapshots-v1.0.1_RC2'),
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$expectedHash = '787F55887C3BBC30E485087E10D2054835DACCE80DDAB920DAF4437564F0E3C2'
$rc2Build = Join-Path $RC2Dir '_camera-workspace-v101-rc2-test\CameraOffsetWorkspaceSnapshotsV101RC2Test\CustomCameraOffset.dll'
$rc2State = Join-Path $RC2Dir '_camera-workspace-v101-rc2-test\test-state.json'
$rc2Installed = Join-Path $GameDir 'Sea Power_Data\StreamingAssets\CameraOffsetWorkspaceSnapshotsV101RC2Test\CustomCameraOffset.dll'
$manualDll = Join-Path $GameDir 'BepInEx\plugins\CustomCameraOffset\CustomCameraOffset.dll'
$workshopDll = Join-Path (Split-Path -Parent (Split-Path -Parent $GameDir)) 'workshop\content\1286220\3844350230\CustomCameraOffset.dll'
$dist = Join-Path $root 'dist'
$packageDir = Join-Path $dist 'CameraOffsetWorkspaceSnapshots'
$packageDll = Join-Path $packageDir 'CustomCameraOffset.dll'
$packageIni = Join-Path $packageDir '_info.ini'
$packageZip = Join-Path $dist 'CameraOffset_WorkspaceSnapshots_v1.0.1_Workshop.zip'
$packageState = Join-Path $dist 'v1.0.1-release-state.json'
$stageDir = Join-Path $GameDir 'Sea Power_Data\StreamingAssets\CameraOffsetWorkspaceSnapshots_v101_Upload'
$stageState = Join-Path $dist 'v1.0.1-workshop-stage.json'
function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant() }
function RequireFile([string]$p) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Expected file is missing: $p" } }
function RequireClosed { if (Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue) { throw 'Fully exit Sea Power before changing files.' } }
function RequireTestDll {
    RequireFile $rc2Build
    $h = HashOf $rc2Build
    if ($h -ne $expectedHash) { throw "RC2 DLL differs from tested build. Expected $expectedHash; got $h" }
    if (Test-Path -LiteralPath $rc2State -PathType Leaf) {
        $s = Get-Content -LiteralPath $rc2State -Raw | ConvertFrom-Json
        if ($s.Version -ne 'CameraOffsetWorkspaceSnapshots-v1.0.1-rc2' -or
            $s.StageDllHash -ne $expectedHash -or $s.GameDir -ine $GameDir) {
            throw 'RC2 test-state does not match the verified DLL/version/game path.'
        }
        RequireFile $rc2Installed
        if ((HashOf $rc2Installed) -ne $expectedHash) { throw 'Installed RC2 test DLL differs from tested build.' }
    }
    Write-Host "TESTED RC2 DLL VERIFIED: $h" -ForegroundColor Green
}
function VerifySources {
    RequireFile (Join-Path $root 'SOURCE_SHA256SUMS.txt')
    $manifest = Get-Content -LiteralPath (Join-Path $root 'SOURCE_SHA256SUMS.txt')
    foreach ($line in $manifest) {
        if ($line -notmatch '^([0-9A-F]{64})  (src/[A-Za-z0-9._/-]+)$') { throw "Invalid source hash entry: $line" }
        $expected = $Matches[1]
        $relative = $Matches[2].Replace('/', '\')
        $path = Join-Path $root $relative
        RequireFile $path
        if ((HashOf $path) -ne $expected) { throw "Tested source changed: $relative" }
    }
    $knownFiles = @($manifest | ForEach-Object { ($_ -split '  ',2)[1] })
    $actualFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File | ForEach-Object { 'src/' + $_.Name })
    if (@($actualFiles | Where-Object { $_ -notin $knownFiles }).Count -ne 0) { throw 'Unexpected source files found.' }
    $plugin = Get-Content -LiteralPath (Join-Path $root 'src\Plugin.cs') -Raw
    if ($plugin -notmatch '\[BepInPlugin\("local\.seapower\.customcameraoffset",\s*"Camera Offset & Workspace Snapshots",\s*"1\.0\.1"\)\]') {
        throw 'Public source is not Camera Offset & Workspace Snapshots v1.0.1.'
    }
}
function VerifyPublicIni {
    $p = Join-Path $root 'workshop\_info.ini'
    RequireFile $p
    $raw = Get-Content -LiteralPath $p -Raw
    if ($raw -notmatch 'Name=Camera Offset & Workspace Snapshots' -or $raw -notmatch 'v1\.0\.1' -or $raw -match 'TEST|RC2') {
        throw 'Public Workshop _info.ini is missing or still labelled TEST.'
    }
    return $p
}
function GetPackage {
    RequireFile $packageState
    $s = Get-Content -LiteralPath $packageState -Raw | ConvertFrom-Json
    if ($s.Version -ne 'v1.0.1' -or $s.DllHash -ne $expectedHash) { throw 'Release state/version/hash mismatch.' }
    RequireFile $packageDll
    RequireFile $packageIni
    RequireFile $packageZip
    if ((HashOf $packageDll) -ne $s.DllHash -or
        (HashOf $packageIni) -ne $s.IniHash -or
        (HashOf $packageZip) -ne $s.ZipHash) { throw 'Release files changed since packaging.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($packageZip)
    try {
        $entries = @($zip.Entries | Where-Object { $_.Name -ne '' } | ForEach-Object { $_.FullName.Replace('\','/') })
        $required = @('CameraOffsetWorkspaceSnapshots/CustomCameraOffset.dll','CameraOffsetWorkspaceSnapshots/_info.ini')
        if ($entries.Count -ne 2 -or @($entries | Where-Object { $_ -notin $required }).Count -gt 0) {
            throw "ZIP content unexpected: $($entries -join '; ')"
        }
    } finally { $zip.Dispose() }
    return $s
}
function RequireNoOtherDlls([string]$allowedDll) {
    $searchFolders = @(
        (Join-Path $GameDir 'BepInEx\plugins'),
        (Join-Path $GameDir 'Sea Power_Data\StreamingAssets'),
        (Join-Path (Split-Path -Parent (Split-Path -Parent $GameDir)) 'workshop\content\1286220')
    )
    $found = @()
    foreach ($f in $searchFolders) {
        if (Test-Path -LiteralPath $f -PathType Container) {
            $found += @(Get-ChildItem -LiteralPath $f -Recurse -File -Filter 'CustomCameraOffset.dll' -ErrorAction Stop | Select-Object -ExpandProperty FullName)
        }
    }
    $unexpected = @($found | Where-Object { $_ -ine $allowedDll })
    if ($unexpected.Count -gt 0) { throw "Conflicting CustomCameraOffset.dll copy found: $($unexpected -join '; ')" }
}
function GetStaging {
    RequireFile $stageState
    $s = Get-Content -LiteralPath $stageState -Raw | ConvertFrom-Json
    if ($s.Version -ne 'v1.0.1' -or $s.Destination -ine $stageDir -or $s.DllHash -ne $expectedHash) {
        throw 'Steam staging state mismatch.'
    }
    if (-not (Test-Path -LiteralPath $stageDir -PathType Container)) { throw 'Staged directory missing.' }
    $items = @(Get-ChildItem -LiteralPath $stageDir -Force)
    if ($items.Count -ne 2 -or @($items | Where-Object { $_.PSIsContainer -or $_.Name -notin @('CustomCameraOffset.dll','_info.ini') }).Count -gt 0) {
        throw 'Unexpected file(s) in Steam staging directory. Do not clean them automatically.'
    }
    $dll = Join-Path $stageDir 'CustomCameraOffset.dll'
    $ini = Join-Path $stageDir '_info.ini'
    if ((HashOf $dll) -ne $s.DllHash -or (HashOf $ini) -ne $s.IniHash) {
        throw 'Staged DLL or _info.ini changed.'
    }
    return $s
}
switch ($Action) {
    'Check' {
        VerifySources
        [void](VerifyPublicIni)
        RequireTestDll
        [pscustomobject]@{
            ReleaseSource = $root
            RC2Build = $rc2Build
            TestStateExists = (Test-Path -LiteralPath $rc2State)
            LocalTestDLLExists = (Test-Path -LiteralPath $rc2Installed)
            ManualPluginExists = (Test-Path -LiteralPath $manualDll)
            WorkshopSubscribedDllExists = (Test-Path -LiteralPath $workshopDll)
            PackageExists = (Test-Path -LiteralPath $packageState)
            StagedForWorkshop = (Test-Path -LiteralPath $stageDir)
        } | Format-List
    }
    'Package' {
        RequireClosed
        VerifySources
        [void](VerifyPublicIni)
        RequireTestDll
        if (Test-Path -LiteralPath $packageZip) { throw "Already exists, refusing to overwrite: $packageZip" }
        if (Test-Path -LiteralPath $packageState) { throw "Release state already exists: $packageState" }
        if (Test-Path -LiteralPath $packageDir) { throw "Release folder already exists: $packageDir" }
        New-Item -Path $dist -ItemType Directory -Force | Out-Null
        New-Item -Path $packageDir -ItemType Directory -Force | Out-Null
        Copy-Item -LiteralPath $rc2Build -Destination $packageDll -ErrorAction Stop
        Copy-Item -LiteralPath (Join-Path $root 'workshop\_info.ini') -Destination $packageIni -ErrorAction Stop
        Compress-Archive -LiteralPath $packageDir -DestinationPath $packageZip -CompressionLevel Optimal -ErrorAction Stop
        $dllHash = HashOf $packageDll
        $iniHash = HashOf $packageIni
        $zipHash = HashOf $packageZip
        [pscustomobject]@{ Version='v1.0.1'; DllHash=$dllHash; IniHash=$iniHash; ZipHash=$zipHash;
            TestedRC2Path=$rc2Build; SourceArchiveType='Tested RC2 DLL plus public Workshop metadata';
            WorkshopItemID='3844350230' } | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath $packageState -Encoding UTF8 -ErrorAction Stop
        @("$dllHash  CameraOffsetWorkspaceSnapshots/CustomCameraOffset.dll", "${zipHash}  CameraOffset_WorkspaceSnapshots_v1.0.1_Workshop.zip") |
            Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding Ascii
        [void](GetPackage)
        Write-Host 'PUBLIC v1.0.1 PACKAGE OK — tested RC2 DLL reused, unchanged.' -ForegroundColor Green
        Write-Host "Workshop ZIP: $packageZip"
        Write-Host "DLL SHA256: $dllHash"
        Write-Host "ZIP SHA256: $zipHash"
    }
    'VerifyPackage' {
        [void](GetPackage)
        Write-Host 'VERIFY PACKAGE OK — DLL, metadata, archive and expected version match.' -ForegroundColor Green
    }
    'Stage' {
        RequireClosed
        $s = GetPackage
        [void]$s
        if ((Test-Path -LiteralPath $rc2State -PathType Leaf) -or (Test-Path -LiteralPath $rc2Installed -PathType Leaf)) {
            throw 'RC2 local test is still installed. Run RC2 -Action Verify then -Action Rollback before Workshop staging.'
        }
        if ((Test-Path -LiteralPath $stageState -PathType Leaf) -or (Test-Path -LiteralPath $stageDir)) {
            throw 'Workshop stage already exists. Inspect it instead of overwriting.'
        }
        RequireNoOtherDlls (Join-Path $stageDir 'CustomCameraOffset.dll')
        New-Item -Path $stageDir -ItemType Directory -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath $packageDll -Destination (Join-Path $stageDir 'CustomCameraOffset.dll') -ErrorAction Stop
        Copy-Item -LiteralPath $packageIni -Destination (Join-Path $stageDir '_info.ini') -ErrorAction Stop
        [pscustomobject]@{ Version='v1.0.1'; DllHash=$expectedHash; IniHash=HashOf $packageIni;
            Destination=$stageDir; WorkshopItemID='3844350230' } |
                ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $stageState -Encoding UTF8 -ErrorAction Stop
        [void](GetStaging)
        Write-Host 'WORKSHOP STAGE OK — exact two-file upload folder created.' -ForegroundColor Green
        Write-Host "Pick Folder: $stageDir"
    }
    'VerifyStage' {
        [void](GetStaging)
        RequireNoOtherDlls (Join-Path $stageDir 'CustomCameraOffset.dll')
        Write-Host 'VERIFY STAGE OK — exact release DLL, public _info.ini, no extra files.' -ForegroundColor Green
    }
    'Clean' {
        RequireClosed
        [void](GetStaging)
        Remove-Item -LiteralPath (Join-Path $stageDir 'CustomCameraOffset.dll') -Force -ErrorAction Stop
        Remove-Item -LiteralPath (Join-Path $stageDir '_info.ini') -Force -ErrorAction Stop
        Remove-Item -LiteralPath $stageDir -Force -ErrorAction Stop
        Remove-Item -LiteralPath $stageState -Force -ErrorAction Stop
        Write-Host 'WORKSHOP STAGE CLEAN OK — tested package and game configuration untouched.' -ForegroundColor Green
    }
    'VerifyWorkshop' {
        RequireClosed
        if (Test-Path -LiteralPath $stageDir) { throw 'Temporary upload stage is still present; run Clean.' }
        if (Test-Path -LiteralPath $rc2Installed) { throw 'Local RC2 test is still installed; run Rollback.' }
        if (Test-Path -LiteralPath $manualDll) { throw 'Manual BepInEx DLL conflict detected.' }
        RequireFile $workshopDll
        $h = HashOf $workshopDll
        if ($h -ne $expectedHash) { throw "Workshop subscriber DLL is NOT v1.0.1. Expected $expectedHash; got $h. Steam may still be downloading." }
        RequireNoOtherDlls $workshopDll
        Write-Host 'WORKSHOP v1.0.1 VERIFY OK — subscribed DLL matches the tested RC2 build.' -ForegroundColor Green
        Write-Host "Workshop location: $workshopDll"
    }
}

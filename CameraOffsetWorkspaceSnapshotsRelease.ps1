# Guarded v1.0.2 public release controller. Windows PowerShell 5.1.
# Reuses the exact, in-game-tested RC1 DLL; no rebuild, no Steam credentials, no upload automation.
[CmdletBinding()]
param(
    [ValidateSet('Check','Package','VerifyPackage','Stage','VerifyStage','Clean','VerifyWorkshop')]
    [string]$Action = 'Check',
    [string]$RC1Dir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'sea-power-camera-offset-workspace-snapshots-v1.0.2_RC1'),
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Sea Power'
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$expectedHash = '57C3C417C12CFED185AA217AEDFC5FE2CF5C32D2A7A2B15DAA7AF737EFBB1E33'
$rc1Build = Join-Path $RC1Dir '_camera-workspace-v102-rc1-test\CameraOffsetWorkspaceSnapshotsV102RC1Test\CustomCameraOffset.dll'
$rc1State = Join-Path $RC1Dir '_camera-workspace-v102-rc1-test\test-state.json'
$rc1Installed = Join-Path $GameDir 'Sea Power_Data\StreamingAssets\CameraOffsetWorkspaceSnapshotsV102RC1Test\CustomCameraOffset.dll'
$manualDll = Join-Path $GameDir 'BepInEx\plugins\CustomCameraOffset\CustomCameraOffset.dll'
$workshopDll = Join-Path (Split-Path -Parent (Split-Path -Parent $GameDir)) 'workshop\content\1286220\3844350230\CustomCameraOffset.dll'
$dist = Join-Path $root 'dist'
$packageDir = Join-Path $dist 'CameraOffsetWorkspaceSnapshots'
$packageDll = Join-Path $packageDir 'CustomCameraOffset.dll'
$packageIni = Join-Path $packageDir '_info.ini'
$packageZip = Join-Path $dist 'CameraOffset_WorkspaceSnapshots_v1.0.2_Workshop.zip'
$packageState = Join-Path $dist 'v1.0.2-release-state.json'
$stageDir = Join-Path $GameDir 'Sea Power_Data\StreamingAssets\CameraOffsetWorkspaceSnapshots_v102_Upload'
$stageState = Join-Path $dist 'v1.0.2-workshop-stage.json'
function HashOf([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToUpperInvariant() }
function RequireFile([string]$p) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw "Expected file is missing: $p" } }
function RequireClosed { if (Get-Process -Name 'Sea Power' -ErrorAction SilentlyContinue) { throw 'Fully exit Sea Power before changing files.' } }
function RequireTestDll {
    RequireFile $rc1Build
    $h = HashOf $rc1Build
    if ($h -ne $expectedHash) { throw "RC1 DLL differs from tested build. Expected $expectedHash; got $h" }
    if (Test-Path -LiteralPath $rc1State -PathType Leaf) {
        $s = Get-Content -LiteralPath $rc1State -Raw | ConvertFrom-Json
        if ($s.Version -ne 'CameraOffsetWorkspaceSnapshots-v1.0.2-rc1' -or
            $s.StageDllHash -ne $expectedHash -or $s.GameDir -ine $GameDir) {
            throw 'RC1 test-state does not match the verified DLL/version/game path.'
        }
        RequireFile $rc1Installed
        if ((HashOf $rc1Installed) -ne $expectedHash) { throw 'Installed RC1 test DLL differs from tested build.' }
    }
    Write-Host "TESTED RC1 DLL VERIFIED: $h" -ForegroundColor Green
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
    if ($plugin -notmatch '\[BepInPlugin\("local\.seapower\.customcameraoffset",\s*"Camera Offset & Workspace Snapshots",\s*"1\.0\.2"\)\]') {
        throw 'Public source is not Camera Offset & Workspace Snapshots v1.0.2.'
    }
}
function VerifyPublicIni {
    $p = Join-Path $root 'workshop\_info.ini'
    RequireFile $p
    $raw = Get-Content -LiteralPath $p -Raw
    if ($raw -notmatch 'Name=Camera Offset & Workspace Snapshots' -or $raw -notmatch 'v1\.0\.2' -or $raw -match 'TEST|RC1') {
        throw 'Public Workshop _info.ini is missing or still labelled TEST.'
    }
    return $p
}
function GetPackage {
    RequireFile $packageState
    $s = Get-Content -LiteralPath $packageState -Raw | ConvertFrom-Json
    if ($s.Version -ne 'v1.0.2' -or $s.DllHash -ne $expectedHash) { throw 'Release state/version/hash mismatch.' }
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
    if ($s.Version -ne 'v1.0.2' -or $s.Destination -ine $stageDir -or $s.DllHash -ne $expectedHash) {
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
            RC1Build = $rc1Build
            TestStateExists = (Test-Path -LiteralPath $rc1State)
            LocalTestDLLExists = (Test-Path -LiteralPath $rc1Installed)
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
        Copy-Item -LiteralPath $rc1Build -Destination $packageDll -ErrorAction Stop
        Copy-Item -LiteralPath (Join-Path $root 'workshop\_info.ini') -Destination $packageIni -ErrorAction Stop
        Compress-Archive -LiteralPath $packageDir -DestinationPath $packageZip -CompressionLevel Optimal -ErrorAction Stop
        $dllHash = HashOf $packageDll
        $iniHash = HashOf $packageIni
        $zipHash = HashOf $packageZip
        [pscustomobject]@{ Version='v1.0.2'; DllHash=$dllHash; IniHash=$iniHash; ZipHash=$zipHash;
            TestedRC1Path=$rc1Build; SourceArchiveType='Tested RC1 DLL plus public Workshop metadata';
            WorkshopItemID='3844350230' } | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath $packageState -Encoding UTF8 -ErrorAction Stop
        @("$dllHash  CameraOffsetWorkspaceSnapshots/CustomCameraOffset.dll", "${zipHash}  CameraOffset_WorkspaceSnapshots_v1.0.2_Workshop.zip") |
            Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding Ascii
        [void](GetPackage)
        Write-Host 'PUBLIC v1.0.2 PACKAGE OK — tested RC1 DLL reused, unchanged.' -ForegroundColor Green
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
        if ((Test-Path -LiteralPath $rc1State -PathType Leaf) -or (Test-Path -LiteralPath $rc1Installed -PathType Leaf)) {
            throw 'RC1 local test is still installed. Run RC1 -Action Verify then -Action Rollback before Workshop staging.'
        }
        if ((Test-Path -LiteralPath $stageState -PathType Leaf) -or (Test-Path -LiteralPath $stageDir)) {
            throw 'Workshop stage already exists. Inspect it instead of overwriting.'
        }
        RequireNoOtherDlls (Join-Path $stageDir 'CustomCameraOffset.dll')
        New-Item -Path $stageDir -ItemType Directory -ErrorAction Stop | Out-Null
        Copy-Item -LiteralPath $packageDll -Destination (Join-Path $stageDir 'CustomCameraOffset.dll') -ErrorAction Stop
        Copy-Item -LiteralPath $packageIni -Destination (Join-Path $stageDir '_info.ini') -ErrorAction Stop
        [pscustomobject]@{ Version='v1.0.2'; DllHash=$expectedHash; IniHash=HashOf $packageIni;
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
        if (Test-Path -LiteralPath $rc1Installed) { throw 'Local RC1 test is still installed; run Rollback.' }
        if (Test-Path -LiteralPath $manualDll) { throw 'Manual BepInEx DLL conflict detected.' }
        RequireFile $workshopDll
        $h = HashOf $workshopDll
        if ($h -ne $expectedHash) { throw "Workshop subscriber DLL is NOT v1.0.2. Expected $expectedHash; got $h. Steam may still be downloading." }
        RequireNoOtherDlls $workshopDll
        Write-Host 'WORKSHOP v1.0.2 VERIFY OK — subscribed DLL matches the tested RC1 build.' -ForegroundColor Green
        Write-Host "Workshop location: $workshopDll"
    }
}

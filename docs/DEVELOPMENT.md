# Development and v1.0.1 release

## Compatibility

The DLL remains `CustomCameraOffset.dll`, plugin GUID `local.seapower.customcameraoffset`, BepInEx config keys preserved. New keys: `[UI] AutoScale` and `[UI] ScalePercent`. Keep the existing Workshop item ID **3844350230**, required Anchor Chain Workshop item **3380210757** and version **1.2**.

## Tested build

The developer compiled and tested v1.0.1 RC2 successfully on Windows with Anchor Chain 1.2. The exact compiled DLL SHA-256 is:

`787F55887C3BBC30E485087E10D2054835DACCE80DDAB920DAF4437564F0E3C2`

The final v1.0.1 source is identical to RC2's `src/` files. The only release changes are repository documentation, the published `_info.ini` and packaging controller. Never claim the build was recompiled in the source ZIP: the original tested DLL must be copied from the developer's RC2 test output.

## 1. Prepare public Workshop ZIP (while RC2 is still installed)

Extract this source package as `C:\Users\fat\Downloads\sea-power-camera-offset-workspace-snapshots-v1.0.1` alongside the existing RC2 source folder. With the game closed, run:

```powershell
cd "C:\Users\fat\Downloads\sea-power-camera-offset-workspace-snapshots-v1.0.1"
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Check
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Package
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action VerifyPackage
```

The controller checks RC2's compiled DLL and its test-state hash, then creates `dist/CameraOffset_WorkspaceSnapshots_v1.0.1_Workshop.zip` with only the tested DLL and clean public `_info.ini`, plus a `SHA256SUMS.txt` and release manifest. An existing output is never silently overwritten.

## 2. Cleanly leave local RC2 testing

First use the *original RC2 folder* (not this release source). With Sea Power closed:

```powershell
cd "C:\Users\fat\Downloads\sea-power-camera-offset-workspace-snapshots-v1.0.1_RC2"
.\CameraOffsetWorkspaceSnapshotsTest.ps1 -Action Verify
.\CameraOffsetWorkspaceSnapshotsTest.ps1 -Action Rollback
```

The original test script recognizes only its own marker and hashes; it never removes unknown files. Leave Anchor Chain installed. Do not reinstall the obsolete v0.9.0 manual BepInEx plugin. Do not resubscribe to Workshop item 3844350230 yet.

## 3. Publish the source on GitHub

After packaging, update the existing GitHub repository with the source from this archive, review the diff, and push a new `v1.0.1` tag. Upload the newly generated Workshop ZIP and checksums to that GitHub release. Keep the existing v1.0.0 release available and do not force-push. Full Git instructions appear below.

## 4. Prepare Steam upload copy

Back in the release-source folder with the game closed:

```powershell
cd "C:\Users\fat\Downloads\sea-power-camera-offset-workspace-snapshots-v1.0.1"
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Stage
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action VerifyStage
```

This creates a controlled temporary upload copy at:
`C:\Program Files (x86)\Steam\steamapps\common\Sea Power\Sea Power_Data\StreamingAssets\CameraOffsetWorkspaceSnapshots_v101_Upload`

Start Sea Power > Mod Manager > Create Mod. Tick **Update Existing**, select the existing **Camera Offset & Workspace Snapshots** entry (Workshop ID **3844350230**), **Pick Folder** and select the exact `CameraOffsetWorkspaceSnapshots_v101_Upload` folder (enter it in the picker if necessary; don't select StreamingAssets itself). Keep existing preview image/title/description, and use `RELEASE_NOTES.md` for the Change Log. Confirm existing-item selection and required Anchor Chain dependency before **Submit Mod**. Do NOT choose Create New or create a second Workshop item. The game's Mod Creation guide confirms the Update Existing flow.

If selecting a preview picture, choose the existing square thumbnail from your **Downloads** folder; do not place the thumbnail into this staging directory.

## 5. Remove upload stage, validate updated subscription

After Steam confirms upload, fully exit Sea Power:

```powershell
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action VerifyStage
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Clean
```

If `Clean` refuses due to extra contents, inspect/move out unexpected files (such as a screenshot added during upload); do not force-delete.

Re-subscribe to Workshop item **3844350230** in Steam and wait for Steam to download it; confirm it is enabled in the Sea Power Mod Manager and only one copy loads. With the game closed, run:

```powershell
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action VerifyWorkshop
```

Launch the game, check the version and slider, and verify original camera/workspace features. If the subscribed DLL hash differs, do not delete or overwrite it: check Steam download status before concluding the upload failed.

## GitHub publishing details

From a **clean clone** of the existing repository, copy the updated source and docs, review the changes, and commit without force-pushing. Example:

```powershell
$source = "$env:USERPROFILE\Downloads\sea-power-camera-offset-workspace-snapshots-v1.0.1"
$repo = "$env:USERPROFILE\Downloads\sea-power-camera-offset-workspace-snapshots-github-v101"
git clone https://github.com/fatmike-slo/sea-power-camera-offset-workspace-snapshots.git $repo
if ($LASTEXITCODE -ne 0) { throw 'Clone failed' }
if ((git -C $repo status --porcelain)) { throw 'Clone is not clean' }
$files = Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]dist[\\/]' }
foreach ($f in $files) {
    $relative = $f.FullName.Substring($source.Length).TrimStart('\')
    $dest = Join-Path $repo $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
}
cd $repo
git status --short
git diff --check
git diff --stat
```

Review the source diff before publishing. The test-only files in the old repository should not be kept if unnecessary. Then:

```powershell
git add README.md CHANGELOG.md RELEASE_NOTES.md SOURCE_SHA256SUMS.txt LICENSE .gitignore docs src tools workshop CameraOffsetWorkspaceSnapshotsRelease.ps1 InspectUiMethods.ps1
git diff --cached --check
git commit -m "Release v1.0.1: adjustable UI scaling"
git push origin HEAD
git tag v1.0.1
git push origin v1.0.1
```

On GitHub, create a release from tag `v1.0.1` and attach `dist/CameraOffset_WorkspaceSnapshots_v1.0.1_Workshop.zip` and `dist/SHA256SUMS.txt`. Use `RELEASE_NOTES.md` as release text. Keep v1.0.0 intact and do not commit the compiled DLL or Sea Power assemblies.

Avoid `git push --force`, removing the repository or making a new Workshop item.

## Rollback after release

The public v1.0.0 GitHub release and its ZIP should remain available. If a regression appears, republish that known-good DLL to the SAME Workshop item using **Update Existing**, then verify Steam subscribers receive the old DLL. Do not activate the legacy `Tactical View Manager` v0.9.0 BepInEx plugin because its GUID conflicts with this mod.

# Building and releasing v1.0.0

These instructions are for the maintainer. Players only need the [README](../README.md).

**Compatibility:** The internal DLL remains `CustomCameraOffset.dll`, plugin GUID `local.seapower.customcameraoffset`, and existing configuration keys remain unchanged. Do not run a manual DLL and a Workshop copy together. The project references Sea Power assemblies from the game installation; none are included in this repository.

## Build and package (no game files changed)

Requires Sea Power with BepInEx, Anchor Chain 1.2, and .NET 8 SDK on the Windows build machine. Keep Sea Power closed.

```powershell
cd "C:\Users\fat\Downloads\sea-power-camera-offset-workspace-snapshots-v1.0.0"
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Check
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Build
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Package
```

A warning from `Check` about the existing WIP4 R1 DLL is expected **before rollback**. Building does not install. The output is `dist/CameraOffset_WorkspaceSnapshots_v1.0.0_Workshop.zip` plus `dist/SHA256SUMS.txt`.

## Upgrade from the working WIP4 R1

Keep both source folders until v1.0.0 is working in-game. With the game closed, use the **original WIP4 R1 controller** to verify and roll back its installation:

```powershell
cd "C:\Users\fat\Downloads\CameraOffset_WorkspaceSnapshots_WIP4_R1_Source"
.\CameraOffsetWorkspaceSnapshotsTest.ps1 -Action Verify
.\CameraOffsetWorkspaceSnapshotsTest.ps1 -Action Rollback
```

If either command fails, stop. Do not delete game files manually.

```powershell
cd "C:\Users\fat\Downloads\sea-power-camera-offset-workspace-snapshots-v1.0.0"
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Check
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Install
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Verify
```

Enable the new mod in Sea Power's mod list and **restart the game**. Test the launcher, shortcut, eight layout icons, camera tracking, four workspace snapshots, mission-start restore, and persistent launcher position. Close the game and verify again. If v1.0.0 fails, with the game closed run:

```powershell
.\CameraOffsetWorkspaceSnapshotsRelease.ps1 -Action Rollback
```

WIP4 R1 can then be reinstalled from its own original source/controller. The new script only removes files it installed and recognizes by recorded state/hash.

## GitHub repository

Repository: `fatmike-slo/sea-power-camera-offset-workspace-snapshots`  
Short description: **Keep your ships in view, arrange the tactical map, and save your favorite layouts in Sea Power.**

Run in the source folder after checking `.gitignore` excludes local builds, DLLs and backups:

```powershell
gh auth status
git init -b main
git add .
git status --short
git commit -m "Camera Offset & Workspace Snapshots v1.0.0"
gh repo create fatmike-slo/sea-power-camera-offset-workspace-snapshots --public --source . --remote origin --push
```

Only create the repo if it does not already exist. No GitHub repository or release is created by these instructions alone.

**After v1.0.0 passes in-game testing**, publish the binary release:

```powershell
git tag -a v1.0.0 -m "Camera Offset & Workspace Snapshots v1.0.0"
git push origin v1.0.0
gh release create v1.0.0 `
  .\dist\CameraOffset_WorkspaceSnapshots_v1.0.0_Workshop.zip `
  .\dist\SHA256SUMS.txt `
  --title "Camera Offset & Workspace Snapshots v1.0.0" `
  --notes "Keep your selected unit in view, switch between eight tactical map layouts, and save four custom workspaces. Enable the mod in Sea Power and restart to use it. Requires Anchor Chain."
```

The predecessor repository `fatmike-slo/sea-power-tactical-view-manager` is separate and must remain untouched until explicitly approved for a later change.

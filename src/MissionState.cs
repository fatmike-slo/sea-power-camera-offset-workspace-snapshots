using System;
using HarmonyLib;
using UnityEngine;

namespace CustomCameraOffset
{
    public sealed partial class Plugin
    {
        // Authoritative game lifecycle hooks, intentionally installed independently
        // of Noesis window hooks. Do not guess gameplay state from the main menu's
        // preview camera or from Unity's active scene name.
        private Harmony? _missionHarmony;
        private bool _missionActive;

        private void InstallMissionStateHooks()
        {
            try
            {
                Type? viewModelType = AccessTools.TypeByName("SeapowerUI.ViewModels.MainGameViewModel");
                Type? gameMainType = AccessTools.TypeByName("SeaPower.GameMain");
                var onStart = viewModelType == null ? null : AccessTools.Method(viewModelType, "OnStart", Type.EmptyTypes);
                var onDestroy = gameMainType == null ? null : AccessTools.Method(gameMainType, "OnDestroy", Type.EmptyTypes);
                if (onStart == null || onDestroy == null)
                {
                    Logger.LogWarning("Gameplay lifecycle hooks unavailable. Ctrl+Shift+U will remain disabled for safety.");
                    return;
                }
                _missionHarmony = new Harmony("local.seapower.customcameraoffset.missionstate");
                _missionHarmony.Patch(onStart, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(Plugin), nameof(OnMissionReadyHook))));
                _missionHarmony.Patch(onDestroy, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(Plugin), nameof(OnMissionEndingHook))));
                Logger.LogInfo("Gameplay lifecycle guard installed: MainGameViewModel.OnStart / GameMain.OnDestroy.");
            }
            catch (Exception ex)
            {
                _missionHarmony?.UnpatchSelf();
                _missionHarmony = null;
                Logger.LogWarning("Gameplay guard setup failed; Ctrl+Shift+U remains disabled: " + ex);
            }
        }

        private static void OnMissionReadyHook() => _instance?.MissionReady();
        private static void OnMissionEndingHook() => _instance?.MissionEnding();

        private void MissionReady()
        {
            _missionActive = true;
            _nextCameraScan = 0f;
            _fullscreenMapSuspended = false;
            _nextFullscreenMapPoll = 0f;
            _pendingLayoutAutoTrim = false;
            ResetAutoCenterObservation();
            WorkspaceMissionReady();
            Logger.LogInfo("Mission began; waiting for the 3D scene camera.");
        }

        private void MissionEnding()
        {
            if (!_missionActive) return;
            // Restore game window geometry while the outgoing mission still exists.
            // Never carry old Noesis objects into the next mission.
            try
            {
                if (_testPositionActive || _splitLayoutActive)
                    RestoreMapAndSplitLayout();
            }
            catch (Exception ex) { Logger.LogWarning("Mission end geometry restore: " + ex.Message); }
            WorkspaceMissionEnding();
            _missionActive = false;
            _pendingLayoutAutoTrim = false;
            _worldFocusPending = false;
            _fullscreenMapSuspended = false;
            _nextFullscreenMapPoll = 0f;
            ClosePanelAndRestoreInput();
            RestoreTrackedProjection();
            _trackedCamera = null;
            _lastTacticalWindow = null;
            _testWindow = null;
            _testView = null;
            _testPositionActive = false;
            _splitLayoutActive = false;
            _cameraStatus = "Waiting for a mission camera";
            ResetAutoCenterObservation();
            if (_pendingSave) SaveSettings();
            Logger.LogInfo("Mission ended; Ctrl+Shift+U, input capture, and temporary map state released.");
        }

        // The current user's v0.6 logs identify the mission world camera as
        // "Main Camera"; menu and loading cameras are excluded explicitly.
        private static bool IsGameWorldCamera(Camera? camera)
        {
            if (camera == null || !IsSuitableCamera(camera)) return false;
            return string.Equals(camera.name, "Main Camera", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsMission3DReady()
        {
            return _missionActive && IsGameWorldCamera(_trackedCamera);
        }

        private void ClosePanelAndRestoreInput()
        {
            bool hadPanel = _visible;
            _visible = false;
            _mouseDragCaptured = false;
            RestoreAllUiHitTesting();
            // Keep the input handoff pending while the cursor is over a map or
            // another floating window. It will complete in the uncovered 3D area.
            if (hadPanel) ArmWorldFocusHandoff();
            if (_pendingSave) SaveSettings();
        }
    }
}

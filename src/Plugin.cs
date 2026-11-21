using System;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomCameraOffset
{
    [BepInPlugin("local.seapower.customcameraoffset", "Camera Offset & Workspace Snapshots", "1.0.0")]
    public sealed partial class Plugin : BaseUnityPlugin
    {
        private const float MaximumOffset = 0.40f; // Fraction of the entire viewport.
        private const float WindowWidth = 442f;
        private const float CollapsedHeight = 522f;
        private const float ExpandedHeight = 760f;
        private const float LauncherWidth = 164f;
        private const float LauncherHeight = 26f;
        private const int LauncherWindowId = 751192;
        private const int WindowId = 751191;

        private ConfigEntry<bool> _enabledSetting = null!;
        private ConfigEntry<float> _horizontalSetting = null!;
        private ConfigEntry<float> _verticalSetting = null!;
        private ConfigEntry<KeyboardShortcut> _toggleHotkeySetting = null!;

        // Old "Camera Presets" config entries are left on disk for rollback.
        // v0.8+ has separate combined layout slots in Saved Layouts.
        private string _presetMessage = "Save a map position, size and camera framing to a named slot.";
        private bool _previousLeftMouseDown;
        private enum SplitLayout { MapLeft, MapRight, MapTop, MapBottom, MapTopLeft, MapBottomLeft, MapTopRight, MapBottomRight }


        private static Plugin? _instance;
        private Harmony? _inputHarmony;
        private bool _visible;
        private bool _mouseDragCaptured;
        private bool _inputErrorReported;
        private bool _recenterOnNextDraw = true;
        private bool _pendingSave;
        private int _previousWidth = -1;
        private int _previousHeight = -1;
        private Rect _window = new Rect(0f, 0f, WindowWidth, CollapsedHeight);
        private Rect _launcherWindow = new Rect(0f, 0f, LauncherWidth, LauncherHeight);
        private ConfigEntry<bool> _launcherEnabledSetting = null!;
        private ConfigEntry<float> _launcherXSetting = null!;
        private ConfigEntry<float> _launcherYSetting = null!;
        private bool _launcherPositionInitialized;
        private bool _launcherMouseCaptured;
        private bool _launcherPendingSave;
        private Vector2 _workspaceScroll;
        private float _horizontal;
        private float _vertical;
        private bool _enabled;

        // Cache the camera's ORIGINAL projection; never continually add an offset
        // onto an already offset matrix (which would cause runaway movement).
        private Camera? _trackedCamera;
        private Matrix4x4 _baseProjection;
        private Matrix4x4 _lastAppliedProjection;
        private bool _hasAppliedProjection;
        private float _cameraFov;
        private float _cameraOrthoSize;
        private float _cameraAspect;
        private float _cameraNear;
        private float _cameraFar;
        private bool _cameraOrthographic;
        private float _nextCameraScan;
        private string _cameraStatus = "No scene camera found";

        private void Awake()
        {
            _instance = this;
            InstallMouseInputGuard();
            InstallMissionStateHooks();

            // Avoid writing the config file on every single mouse-drag frame.
            Config.SaveOnConfigSet = false;
            _enabledSetting = Config.Bind("Camera Offset", "Enabled", true,
                "If false, restore the game's original camera projection.");
            _horizontalSetting = Config.Bind("Camera Offset", "Horizontal", 0f,
                "Horizontal shift as a fraction of the viewport (-0.4 to 0.4). Positive moves the subject right.");
            _verticalSetting = Config.Bind("Camera Offset", "Vertical", 0f,
                "Vertical shift as a fraction of the viewport (-0.4 to 0.4). Positive moves the subject up.");
            _toggleHotkeySetting = Config.Bind("General", "ToggleHotkey",
                new KeyboardShortcut(KeyCode.U, KeyCode.LeftControl, KeyCode.LeftShift),
                "Open/close the Camera Offset & Workspace Snapshots panel. Default: Left Ctrl + Left Shift + U. Change this value if another mod uses the same shortcut.");
            _launcherEnabledSetting = Config.Bind("General", "ShowMiniLauncher", true,
                "Show the compact Camera + Workspace launcher in missions. Ctrl+Shift+U remains available when hidden.");
            _launcherXSetting = Config.Bind("General", "MiniLauncherX", -1f,
                "Normalized horizontal position of the small launcher; -1 uses upper right.");
            _launcherYSetting = Config.Bind("General", "MiniLauncherY", -1f,
                "Normalized vertical position of the small launcher; -1 uses near the top.");
            BindSavedLayouts();
            BindAutoCenterConfig();
            BindWorkspaceConfig();

            _enabled = _enabledSetting.Value;
            _horizontal = Mathf.Clamp(_horizontalSetting.Value, -MaximumOffset, MaximumOffset);
            _vertical = Mathf.Clamp(_verticalSetting.Value, -MaximumOffset, MaximumOffset);
            LoadAutoCenterConfig();

            // Built-in render pipeline callback plus SRP callback. Both are harmless
            // if the other pipeline is active. Also apply in LateUpdate as a backup.
            Camera.onPreCull += OnCameraPreCull;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            Logger.LogInfo("Camera Offset & Workspace Snapshots v1.0.0 loaded; labeled mini launcher, graphical quick layouts and collapsible snapshots.");
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= OnCameraPreCull;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RestoreTrackedProjection();
            RestoreAllUiHitTesting();
            _worldFocusPending = false;
            _pendingLayoutAutoTrim = false;
            _inputHarmony?.UnpatchSelf();
            _mapHarmony?.UnpatchSelf();
            _missionHarmony?.UnpatchSelf();
            if (ReferenceEquals(_instance, this)) _instance = null;
            if (_pendingSave) SaveSettings();
            ClearWorkspaceRuntimeState();
        }

        private void Update()
        {
            try
            {
                if (_visible && !IsMission3DReady()) ClosePanelAndRestoreInput();
                if (_toggleHotkeySetting.Value.IsDown() && IsMission3DReady() &&
                    !(_workspacePage && GUIUtility.keyboardControl != 0))
                {
                    if (_visible) ClosePanelAndRestoreInput();
                    else
                    {
                        _visible = true;
                        _recenterOnNextDraw = true;
                    }
                }
                // An outside click closes our panel and restores Noesis hit testing.
                // The focus-handoff helper subsequently clears Noesis focus only
                // when the cursor reaches free 3D space, not while over game UI.
                bool leftDown = IsLeftMouseDown();
                if (_visible && Application.isFocused && leftDown && !_previousLeftMouseDown &&
                    !_mouseDragCaptured && !PointerWithinWindow() &&
                    !_launcherMouseCaptured && !PointerWithinLauncher())
                    ClosePanelAndRestoreInput();
                _previousLeftMouseDown = leftDown;
                if (!leftDown && _launcherPendingSave) SaveLauncherPosition();
                PollWorldFocusHandoff();
                UpdateMouseCapture();
                UpdateLauncherMouseCapture();
                PollTacticalWindowHook();
                PollWorkspaceRestoration();
                PollFullscreenMapState();
                GuardAllUiHitTesting();
                // GetMouseButtonUp is guarded as well, so use the physical mouse state
                // to save drag changes without losing the release event.
                if (_pendingSave && !IsLeftMouseDown()) SaveSettings();
            }
            catch (InvalidOperationException ex)
            {
                if (!_inputErrorReported)
                {
                    Logger.LogError("Legacy input unavailable: " + ex.Message);
                    _inputErrorReported = true;
                }
            }
        }

        private void LateUpdate()
        {
            if (!_missionActive)
            {
                RestoreTrackedProjection();
                _trackedCamera = null;
                _autoCalculated = false;
                return;
            }
            // The game can replace its mission camera when switching units/scenes.
            if (Time.unscaledTime >= _nextCameraScan || !IsSuitableCamera(_trackedCamera))
            {
                _nextCameraScan = Time.unscaledTime + 0.25f;
                SelectCamera();
            }
            if (!IsMission3DReady())
            {
                if (_visible) ClosePanelAndRestoreInput();
                RestoreTrackedProjection();
                return;
            }
            PollAutoCenter();
            if (_fullscreenMapSuspended)
            {
                // The game owns the 3D PiP projection while Tactical Display is fullscreen.
                // No user settings or saved camera presets are changed.
                RestoreTrackedProjection();
                return;
            }
            if (!_enabled || (Mathf.Abs(EffectiveHorizontal) < 0.0001f && Mathf.Abs(EffectiveVertical) < 0.0001f))
            {
                RestoreTrackedProjection();
                return;
            }
            ApplyProjection(_trackedCamera);
        }

        private static bool IsSuitableCamera(Camera? camera)
        {
            return camera != null && camera.isActiveAndEnabled && camera.targetTexture == null &&
                   camera.pixelWidth > 0 && camera.pixelHeight > 0;
        }

        private void SelectCamera()
        {
            Camera? candidate = Camera.main;
            // Never select the loading or main-menu camera for projection changes.
            if (!IsSuitableCamera(candidate) || !IsGameWorldCamera(candidate))
            {
                candidate = null;
                float bestScore = float.MinValue;
                // Tactical-map, mirror and preview cameras frequently render to textures.
                // Never modify those. Prefer a full-screen scene camera instead.
                foreach (Camera cam in Camera.allCameras)
                {
                    if (!IsSuitableCamera(cam) || !IsGameWorldCamera(cam)) continue;
                    float coverage = (float)cam.pixelWidth / Math.Max(1, Screen.width) *
                                     (float)cam.pixelHeight / Math.Max(1, Screen.height);
                    if (coverage < 0.45f) continue;
                    float score = coverage * 100f + cam.depth;
                    if (score <= bestScore) continue;
                    bestScore = score;
                    candidate = cam;
                }
            }
            if (candidate == _trackedCamera) return;

            RestoreTrackedProjection();
            _trackedCamera = candidate;
            _hasAppliedProjection = false;
            _cameraStatus = candidate != null ? candidate.name : "No active scene camera";
            Logger.LogInfo("Target scene camera: " + _cameraStatus);
        }

        private void OnCameraPreCull(Camera camera)
        {
            if (_missionActive && !_fullscreenMapSuspended && camera == _trackedCamera && _enabled) ApplyProjection(camera);
        }

        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_missionActive && !_fullscreenMapSuspended && camera == _trackedCamera && _enabled) ApplyProjection(camera);
        }

        private bool CameraPropertiesChanged(Camera camera)
        {
            return !Mathf.Approximately(_cameraFov, camera.fieldOfView) ||
                   !Mathf.Approximately(_cameraOrthoSize, camera.orthographicSize) ||
                   !Mathf.Approximately(_cameraAspect, camera.aspect) ||
                   !Mathf.Approximately(_cameraNear, camera.nearClipPlane) ||
                   !Mathf.Approximately(_cameraFar, camera.farClipPlane) ||
                   _cameraOrthographic != camera.orthographic;
        }

        private void CaptureCameraProperties(Camera camera)
        {
            _cameraFov = camera.fieldOfView;
            _cameraOrthoSize = camera.orthographicSize;
            _cameraAspect = camera.aspect;
            _cameraNear = camera.nearClipPlane;
            _cameraFar = camera.farClipPlane;
            _cameraOrthographic = camera.orthographic;
        }

        private static bool ApproximatelySameMatrix(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
            {
                if (Mathf.Abs(a[i] - b[i]) > 0.00001f) return false;
            }
            return true;
        }

        private void ApplyProjection(Camera camera)
        {
            if (!_missionActive || _fullscreenMapSuspended || !IsSuitableCamera(camera) || !IsGameWorldCamera(camera)) return;
            float viewX = EffectiveHorizontal;
            float viewY = EffectiveVertical;
            if (Mathf.Abs(viewX) < 0.0001f && Mathf.Abs(viewY) < 0.0001f)
            {
                RestoreTrackedProjection();
                return;
            }

            Matrix4x4 current = camera.projectionMatrix;
            if (_hasAppliedProjection && CameraPropertiesChanged(camera) &&
                ApproximatelySameMatrix(current, _lastAppliedProjection))
            {
                // Once a custom projection is assigned, Unity no longer automatically
                // regenerates it on zoom/FOV changes. Rebuild the native projection.
                camera.ResetProjectionMatrix();
                current = camera.projectionMatrix;
                _hasAppliedProjection = false;
            }

            // If the game changed the matrix itself, treat its new matrix as baseline.
            if (!_hasAppliedProjection || !ApproximatelySameMatrix(current, _lastAppliedProjection))
            {
                _baseProjection = current;
            }
            CaptureCameraProperties(camera);

            // Premultiply a clip-space translation. That adds X/Y in normalised screen
            // coordinates to BOTH perspective and orthographic projection matrices.
            // Camera position/rotation, world tracking and distance stay untouched.
            Matrix4x4 shift = Matrix4x4.identity;
            shift[0, 3] = 2f * viewX;
            shift[1, 3] = 2f * viewY;
            Matrix4x4 adjusted = shift * _baseProjection;
            if (!ApproximatelySameMatrix(current, adjusted)) camera.projectionMatrix = adjusted;
            _lastAppliedProjection = adjusted;
            _hasAppliedProjection = true;
        }

        private void RestoreTrackedProjection()
        {
            if (_trackedCamera != null && _hasAppliedProjection)
            {
                // Do not overwrite a different projection subsequently installed by the game.
                if (ApproximatelySameMatrix(_trackedCamera.projectionMatrix, _lastAppliedProjection))
                {
                    if (CameraPropertiesChanged(_trackedCamera))
                        _trackedCamera.ResetProjectionMatrix();
                    else
                        _trackedCamera.projectionMatrix = _baseProjection;
                }
            }
            _hasAppliedProjection = false;
        }

        private void SaveSettings()
        {
            _enabledSetting.Value = _enabled;
            _horizontalSetting.Value = _horizontal;
            _verticalSetting.Value = _vertical;
            SaveAutoCenterConfig();
            Config.Save();
            _pendingSave = false;
        }

        private void ChangeOffset(float x, float y)
        {
            x = Mathf.Clamp(x, -MaximumOffset, MaximumOffset);
            y = Mathf.Clamp(y, -MaximumOffset, MaximumOffset);
            if (Mathf.Approximately(x, _horizontal) && Mathf.Approximately(y, _vertical)) return;
            _horizontal = x;
            _vertical = y;
            _pendingSave = true;
            _presetMessage = "View changed; Save to a layout slot to keep the map + camera.";
            if (_trackedCamera != null && _enabled) ApplyProjection(_trackedCamera);
        }

        // Mouse capture: Unity's IMGUI does not automatically prevent the game from
        // polling its own mouse buttons/axes during Update. HarmonyX patches only
        // Unity's legacy *mouse* polls, and only while our panel needs the mouse.
        // No game DLL, camera controller, or keybinding is modified.
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);

        private static bool IsLeftMouseDown()
        {
            // Sea Power is running on Windows in this installation.
            return (GetAsyncKeyState(0x01) & 0x8000) != 0;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                _mouseDragCaptured = false;
                _launcherMouseCaptured = false;
                _previousLeftMouseDown = false;
                _worldFocusPending = false;
                RestoreAllUiHitTesting();
            }
        }

        private void UpdateMouseCapture()
        {
            if (!_visible || !Application.isFocused)
            {
                _mouseDragCaptured = false;
                return;
            }
            bool down = IsLeftMouseDown();
            if (!down)
            {
                _mouseDragCaptured = false;
                return;
            }
            // Keep capture until release even if the cursor moves outside while
            // dragging the slider or moving the window by its title bar.
            if (PointerWithinWindow()) _mouseDragCaptured = true;
        }

        private bool PointerWithinWindow()
        {
            // Input.mousePosition has bottom-left origin; IMGUI Rect has top-left.
            Vector3 pos = Input.mousePosition;
            return _window.Contains(new Vector2(pos.x, Screen.height - pos.y));
        }

        private bool PointerWithinLauncher()
        {
            if (!_launcherEnabledSetting.Value || !IsMission3DReady()) return false;
            Vector3 p = Input.mousePosition;
            return _launcherWindow.Contains(new Vector2(p.x, Screen.height - p.y));
        }

        private void UpdateLauncherMouseCapture()
        {
            if (!Application.isFocused || !_launcherEnabledSetting.Value || !IsMission3DReady())
            {
                _launcherMouseCaptured = false;
                return;
            }
            if (!IsLeftMouseDown()) _launcherMouseCaptured = false;
            else if (PointerWithinLauncher()) _launcherMouseCaptured = true;
        }

        private void SaveLauncherPosition()
        {
            float maxX = Mathf.Max(1f, Screen.width - LauncherWidth);
            float maxY = Mathf.Max(1f, Screen.height - LauncherHeight);
            _launcherXSetting.Value = Mathf.Clamp01(_launcherWindow.x / maxX);
            _launcherYSetting.Value = Mathf.Clamp01(_launcherWindow.y / maxY);
            _launcherPendingSave = false;
            SaveSettings();
        }

        private static bool WantsMouseInput()
        {
            Plugin? plugin = _instance;
            return plugin != null && Application.isFocused &&
                   ((plugin._visible && (plugin._mouseDragCaptured || plugin.PointerWithinWindow())) ||
                    (plugin._launcherEnabledSetting.Value &&
                     (plugin._launcherMouseCaptured || plugin.PointerWithinLauncher())));
        }

        private static bool IsMouseKey(KeyCode key)
        {
            return key >= KeyCode.Mouse0 && key <= KeyCode.Mouse6;
        }

        private static bool IsMouseName(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            // Unity legacy defaults. Preserve gameplay keyboard and other axes.
            return name.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.Equals("Fire1", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Fire2", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("Fire3", StringComparison.OrdinalIgnoreCase);
        }

        private static bool BlockMouseButton(ref bool __result)
        {
            if (!WantsMouseInput()) return true;
            __result = false;
            return false;
        }

        private static bool BlockMouseKey(KeyCode __0, ref bool __result)
        {
            if (!WantsMouseInput() || !IsMouseKey(__0)) return true;
            __result = false;
            return false;
        }

        private static bool BlockMouseKeyName(string __0, ref bool __result)
        {
            if (!WantsMouseInput() || !IsMouseName(__0)) return true;
            __result = false;
            return false;
        }

        private static bool BlockMouseAxis(string __0, ref float __result)
        {
            if (!WantsMouseInput() || !IsMouseName(__0)) return true;
            __result = 0f;
            return false;
        }

        private static bool BlockMouseScroll(ref Vector2 __result)
        {
            if (!WantsMouseInput()) return true;
            __result = Vector2.zero;
            return false;
        }

        private void InstallMouseInputGuard()
        {
            _inputHarmony = new Harmony("local.seapower.customcameraoffset.mouseguard");
            int installed = 0;

            void Patch(string method, Type[] args, string prefixMethod)
            {
                try
                {
                    MethodInfo? target = typeof(Input).GetMethod(method,
                        BindingFlags.Public | BindingFlags.Static, null, args, null);
                    MethodInfo? prefix = typeof(Plugin).GetMethod(prefixMethod,
                        BindingFlags.NonPublic | BindingFlags.Static);
                    if (target == null || prefix == null)
                    {
                        Logger.LogWarning("Mouse guard method not available: " + method);
                        return;
                    }
                    _inputHarmony.Patch(target, prefix: new HarmonyMethod(prefix));
                    installed++;
                }
                catch (Exception ex)
                {
                    // A failed interception must never prevent camera offset working.
                    Logger.LogWarning("Mouse guard could not patch " + method + ": " + ex.Message);
                }
            }

            foreach (string name in new[] { "GetMouseButton", "GetMouseButtonDown", "GetMouseButtonUp" })
                Patch(name, new[] { typeof(int) }, nameof(BlockMouseButton));
            foreach (string name in new[] { "GetKey", "GetKeyDown", "GetKeyUp" })
            {
                Patch(name, new[] { typeof(KeyCode) }, nameof(BlockMouseKey));
                Patch(name, new[] { typeof(string) }, nameof(BlockMouseKeyName));
            }
            foreach (string name in new[] { "GetButton", "GetButtonDown", "GetButtonUp" })
                Patch(name, new[] { typeof(string) }, nameof(BlockMouseKeyName));
            foreach (string name in new[] { "GetAxis", "GetAxisRaw" })
                Patch(name, new[] { typeof(string) }, nameof(BlockMouseAxis));
            Patch("get_mouseScrollDelta", Type.EmptyTypes, nameof(BlockMouseScroll));

            Logger.LogInfo("Mouse input guard: " + installed + " Unity legacy input hooks installed.");
            if (installed == 0)
                Logger.LogWarning("No mouse input hooks installed; the game may still rotate behind the panel.");
        }

        private void OnGUI()
        {
            if (!IsMission3DReady()) return;

            if (_launcherEnabledSetting.Value)
            {
                if (!_launcherPositionInitialized)
                {
                    float maxX = Mathf.Max(0f, Screen.width - LauncherWidth);
                    float maxY = Mathf.Max(0f, Screen.height - LauncherHeight);
                    _launcherWindow.x = _launcherXSetting.Value < 0f ?
                        Mathf.Max(0f, Screen.width - LauncherWidth - 36f) : Mathf.Clamp01(_launcherXSetting.Value) * maxX;
                    _launcherWindow.y = _launcherYSetting.Value < 0f ?
                        Mathf.Min(46f, maxY) : Mathf.Clamp01(_launcherYSetting.Value) * maxY;
                    _launcherPositionInitialized = true;
                }
                _launcherWindow.x = Mathf.Clamp(_launcherWindow.x, 0f, Mathf.Max(0f, Screen.width - LauncherWidth));
                _launcherWindow.y = Mathf.Clamp(_launcherWindow.y, 0f, Mathf.Max(0f, Screen.height - LauncherHeight));
                int depth = GUI.depth;
                GUI.depth = -1100;
                Rect before = _launcherWindow;
                _launcherWindow = GUI.Window(LauncherWindowId, _launcherWindow, DrawLauncher,
                    "", GUI.skin.box);
                if (!Mathf.Approximately(before.x, _launcherWindow.x) ||
                    !Mathf.Approximately(before.y, _launcherWindow.y))
                    _launcherPendingSave = true;
                GUI.depth = depth;
            }

            if (!_visible) return;
            float availableHeight = Mathf.Max(480f, Screen.height - 16f);
            float desiredHeight = _workspacePage ? ExpandedHeight : CollapsedHeight;
            _window.height = Mathf.Min(desiredHeight, availableHeight);
            _window.width = WindowWidth;
            if (_recenterOnNextDraw || Screen.width != _previousWidth || Screen.height != _previousHeight)
            {
                _window.x = Mathf.Max(0f, (Screen.width - WindowWidth) / 2f);
                _window.y = Mathf.Max(0f, (Screen.height - _window.height) / 2f);
                _previousWidth = Screen.width;
                _previousHeight = Screen.height;
                _recenterOnNextDraw = false;
            }
            _window.x = Mathf.Clamp(_window.x, 0f, Mathf.Max(0f, Screen.width - WindowWidth));
            _window.y = Mathf.Clamp(_window.y, 0f, Mathf.Max(0f, Screen.height - _window.height));
            int oldGuiDepth = GUI.depth;
            GUI.depth = -1000;
            _window = GUI.Window(WindowId, _window, DrawWindow,
                "Camera Offset & Workspace Snapshots v1.0.0");
            GUI.BringWindowToFront(WindowId);
            Event ev = Event.current;
            if (ev != null && ev.isMouse &&
                (_window.Contains(ev.mousePosition) || _mouseDragCaptured) &&
                ev.type != EventType.Used && ev.type != EventType.Layout &&
                ev.type != EventType.Repaint)
                ev.Use();
            GUI.depth = oldGuiDepth;
        }

        private void DrawLauncher(int id)
        {
            // Tiny, separate action and drag handle so the player does not need
            // to remember the keyboard shortcut or accidentally drag while clicking.
            if (GUI.Button(new Rect(2f, 2f, 143f, 22f),
                new GUIContent("Camera + Workspace", "Camera Offset & Workspace Snapshots (Ctrl+Shift+U)")))
            {
                if (_visible) ClosePanelAndRestoreInput();
                else
                {
                    _visible = true;
                    _recenterOnNextDraw = true;
                }
            }
            GUI.Label(new Rect(148f, 3f, 12f, 19f), ":");
            GUI.DragWindow(new Rect(145f, 0f, 19f, LauncherHeight));
        }

        private void DrawWindow(int windowId)
        {
            // Camera and map arrangement remain visible at all times.
            GUI.Box(new Rect(13f, 30f, WindowWidth - 26f, 164f), "");
            GUI.Label(new Rect(22f, 33f, 396f, 19f), "CAMERA FRAMING");
            bool enabledNow = GUI.Toggle(new Rect(18f, 53f, 179f, 23f),
                _enabled, "Enable camera offset");
            if (enabledNow != _enabled)
            {
                _enabled = enabledNow;
                if (!_enabled) RestoreTrackedProjection();
                _pendingSave = true;
                SaveSettings();
            }
            bool autoNow = GUI.Toggle(new Rect(205f, 53f, 214f, 23f),
                _autoCenterEnabled, "Auto-center around map");
            if (autoNow != _autoCenterEnabled) SetAutoCenterEnabled(autoNow);
            GUI.Label(new Rect(20f, 79f, 399f, 18f),
                (_autoCenterEnabled ? "Fine-tune horizontal: " : "Horizontal: ") +
                Mathf.RoundToInt((_autoCenterEnabled ? _autoTrimX : _horizontal) * 100f) + "%  (left / right)");
            float nextX = GUI.HorizontalSlider(new Rect(21f, 99f, 398f, 18f),
                _autoCenterEnabled ? _autoTrimX : _horizontal, -MaximumOffset, MaximumOffset);
            GUI.Label(new Rect(20f, 120f, 399f, 18f),
                (_autoCenterEnabled ? "Fine-tune vertical: " : "Vertical: ") +
                Mathf.RoundToInt((_autoCenterEnabled ? _autoTrimY : _vertical) * 100f) + "%  (down / up)");
            float nextY = GUI.HorizontalSlider(new Rect(21f, 140f, 398f, 18f),
                _autoCenterEnabled ? _autoTrimY : _vertical, -MaximumOffset, MaximumOffset);
            if (_autoCenterEnabled) ChangeAutoTrim(nextX, nextY);
            else ChangeOffset(nextX, nextY);
            GUI.Label(new Rect(20f, 165f, 293f, 21f), _autoStatus);
            if (GUI.Button(new Rect(325f, 161f, 94f, 26f), "Reset offsets"))
            {
                if (_autoCenterEnabled) ChangeAutoTrim(0f, 0f);
                else ChangeOffset(0f, 0f);
                _presetMessage = _autoCenterEnabled ?
                    "Reset auto fine-tune to zero." : "Reset to default camera framing.";
                if (!_autoCenterEnabled) RestoreTrackedProjection();
                SaveSettings();
            }

            // Original v0.9.0 graphical tiles, not text-only buttons.
            GUI.Box(new Rect(13f, 202f, WindowWidth - 26f, 184f), "");
            GUI.Label(new Rect(22f, 207f, 390f, 21f), "QUICK MAP + 3D LAYOUTS");
            DrawQuickMapTile(SplitLayout.MapLeft, 21f, 231f, "Map left");
            DrawQuickMapTile(SplitLayout.MapRight, 123f, 231f, "Map right");
            DrawQuickMapTile(SplitLayout.MapTop, 225f, 231f, "Map top");
            DrawQuickMapTile(SplitLayout.MapBottom, 327f, 231f, "Map bottom");
            DrawQuickMapTile(SplitLayout.MapTopLeft, 21f, 283f, "Top left");
            DrawQuickMapTile(SplitLayout.MapTopRight, 123f, 283f, "Top right");
            DrawQuickMapTile(SplitLayout.MapBottomLeft, 225f, 283f, "Bottom left");
            DrawQuickMapTile(SplitLayout.MapBottomRight, 327f, 283f, "Bottom right");
            if (GUI.Button(new Rect(22f, 340f, 396f, 24f), "Restore previous map + camera"))
                RestoreMapAndSplitLayout();
            GUI.Label(new Rect(23f, 365f, 392f, 17f),
                "Quick layouts do not move other windows.");

            // Foldout: snapshots hidden by default. No separate full-screen page.
            float footerY = _window.height - 64f;
            float foldoutBottom = footerY - 8f;
            GUI.Box(new Rect(13f, 394f, WindowWidth - 26f,
                Mathf.Max(38f, foldoutBottom - 394f)), "");
            if (GUI.Button(new Rect(21f, 399f, 399f, 27f),
                _workspacePage ? "-  MY WORKSPACES  |  Hide snapshots" :
                    "+  MY WORKSPACES  |  4 saved snapshots"))
                _workspacePage = !_workspacePage;
            if (_workspacePage)
                DrawInlineWorkspaces(footerY);

            // Three separate footer rows eliminate the former clipping/overlap.
            GUI.Label(new Rect(20f, footerY + 3f, 401f, 19f),
                _workspacePage ? _workspaceMessage : _tacticalStatus);
            bool miniNow = GUI.Toggle(new Rect(20f, footerY + 25f, 145f, 22f),
                _launcherEnabledSetting.Value, "Show mini button");
            if (miniNow != _launcherEnabledSetting.Value)
            {
                _launcherEnabledSetting.Value = miniNow;
                SaveSettings();
            }
            GUI.Label(new Rect(169f, footerY + 27f, 166f, 18f), "Ctrl+Shift+U");
            if (GUI.Button(new Rect(WindowWidth - 101f, footerY + 21f, 83f, 26f), "Close"))
                ClosePanelAndRestoreInput();
            GUI.DragWindow(new Rect(0f, 0f, WindowWidth, 25f));
        }
    }
}

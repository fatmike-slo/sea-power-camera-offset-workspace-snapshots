using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Noesis;
using SeaPower;
using SeapowerUI;
using UnityEngine;
using GameWindow = SeapowerUI.Window;

namespace CustomCameraOffset
{
    // v0.6: reversible tactical-display sizing, 50/50 layouts and hit-test isolation.
    // Existing game UI remains intact; geometry is changed only by explicit clicks.
    public sealed partial class Plugin
    {
        private Harmony? _mapHarmony;
        private bool _mapHooksInstalled;
        private bool _mapHookFailed;
        private float _nextMapHookCheck;
        private WeakReference<GameWindow>? _lastTacticalWindow;
        private GameWindow? _testWindow;
        private MainGameView? _testView;
        private float _oldMapX;
        private float _oldMapY;
        private bool _oldMapAnchorBottom;
        private bool _oldMapAnchorRight;
        private bool _testPositionActive;
        private float _oldMapWidth;
        private float _oldMapHeight;
        // Deliberately session-only. Size is applied on button click, not each slider frame.
        private float _mapWidthFraction = 0.50f;
        private float _mapHeightFraction = 0.50f;
        private string _tacticalStatus = "Open Tactical Display in a mission first.";

        // Unity IMGUI and Sea Power's Noesis UI receive mouse input independently.
        // Treat our Ctrl+Shift+U window as a temporary modal *mouse region*: while its
        // pointer/drag capture is active, suspend Noesis hit-testing and restore
        // every original flag as soon as it is no longer needed. This protects
        // the map, Formation Manager, Event Log, and other game windows alike.
        private sealed class WindowHitTestSnapshot
        {
            public readonly GameWindow Window;
            public readonly bool WasEnabled;
            public WindowHitTestSnapshot(GameWindow window, bool wasEnabled)
            { Window = window; WasEnabled = wasEnabled; }
        }
        private readonly List<WindowHitTestSnapshot> _suppressedWindows = new List<WindowHitTestSnapshot>();
        private readonly List<WeakReference<GameWindow>> _observedWindows = new List<WeakReference<GameWindow>>();
        private UIElement? _suppressedUiRoot;
        private bool _uiRootWasEnabled;
        private bool _uiGuardWasActive;
        private float _nextUiGuardScan;
        private bool _uiGuardWarningIssued;
        private bool _splitLayoutActive;
        private float _cameraBeforeSplitX;
        private float _cameraBeforeSplitY;
        private bool _cameraBeforeSplitEnabled;

        // IMPORTANT: The Noesis window's static initializer registers native dependency
        // properties. Patching it before Noesis starts can crash the game. Wait until
        // WindowHostManager has an instance, as the reference window-management mod does.
        private void PollTacticalWindowHook()
        {
            if (_mapHooksInstalled || _mapHookFailed || Time.unscaledTime < _nextMapHookCheck)
                return;
            _nextMapHookCheck = Time.unscaledTime + 0.5f;
            try
            {
                if (!Singleton<WindowHostManager>.InstanceExists()) return;
                MethodInfo? target = AccessTools.Method(typeof(GameWindow), "OnInitialized", Type.EmptyTypes);
                MethodInfo? postfix = AccessTools.Method(typeof(Plugin), nameof(OnTacticalWindowInitialized));
                if (target == null || postfix == null)
                {
                    _mapHookFailed = true;
                    _tacticalStatus = "Window hook unavailable; consult BepInEx log.";
                    Logger.LogWarning("Tactical Display: Window.OnInitialized is unavailable.");
                    return;
                }
                _mapHarmony = new Harmony("local.seapower.customcameraoffset.tacticaltest");
                _mapHarmony.Patch(target, postfix: new HarmonyMethod(postfix));
                _mapHooksInstalled = true;
                Logger.LogInfo("Tactical Display: Noesis window hook installed after UI initialization.");
            }
            catch (Exception ex)
            {
                _mapHookFailed = true;
                _tacticalStatus = "Window hook failed; see BepInEx log.";
                Logger.LogWarning("Tactical Display: failed to register window hook: " + ex);
            }
        }

        private static void OnTacticalWindowInitialized(GameWindow __instance)
        {
            try { _instance?.ObserveTacticalWindow(__instance); }
            catch (Exception ex) { _instance?.Logger.LogWarning("Tactical window observation: " + ex.Message); }
        }

        private void ObserveTacticalWindow(GameWindow window)
        {
            TrackGameWindow(window);
            if (IsTacticalDisplay(window))
            {
                _lastTacticalWindow = new WeakReference<GameWindow>(window);
                _tacticalStatus = "Map detected. Ready for geometry test.";
                Logger.LogInfo("Tactical Display window observed: " + window.Name);
            }
        }

        private static object? MapContext(GameWindow window)
        {
            object? context = window.DataContext;
            if (context == null && window.Content is FrameworkElement element)
                context = element.DataContext;
            return context;
        }

        private static bool IsTacticalDisplay(GameWindow window)
        {
            object? context = MapContext(window);
            string typeName = context?.GetType().Name ?? "";
            if (typeName == "MapViewModel" || typeName == "SceneMapViewModel") return true;
            string header = HeaderText(window.Header);
            return header.IndexOf("Tactical Display", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string HeaderText(object? header, int depth = 0)
        {
            if (header is string text) return text;
            if (header is TextBlock block) return block.Text ?? "";
            if (header is ContentControl control && depth < 3)
                return HeaderText(control.Content, depth + 1);
            return "";
        }

        private static bool IsFullscreenTacticalDisplay(GameWindow window)
        {
            object? context = MapContext(window);
            if (context == null) return false;
            try
            {
                PropertyInfo? prop = context.GetType().GetProperty("IsFullscreen", BindingFlags.Public | BindingFlags.Instance);
                return prop != null && prop.PropertyType == typeof(bool) && (bool)(prop.GetValue(context) ?? false);
            }
            catch { return false; }
        }

        // Fullscreen map and its optional World View / PiP use the game's own
        // rendering layout. Suspend BOTH automatic and manual projection offsets.
        // Do not alter the user's persistent Enabled, trim, or preset settings.
        private bool _fullscreenMapSuspended;
        private float _nextFullscreenMapPoll;

        private void PollFullscreenMapState()
        {
            if (!_missionActive)
            {
                _fullscreenMapSuspended = false;
                _nextFullscreenMapPoll = 0f;
                return;
            }
            if (Time.unscaledTime < _nextFullscreenMapPoll) return;
            _nextFullscreenMapPoll = Time.unscaledTime + 0.10f;
            try
            {
                GameWindow? map = FindTacticalDisplay();
                // In some game versions Tab sets MapViewModel.IsFullscreen;
                // in others the game window exposes a maximized state.
                bool fullscreen = map != null && map.IsLoaded &&
                    (map.Maximized || IsFullscreenTacticalDisplay(map));
                if (fullscreen == _fullscreenMapSuspended) return;

                _fullscreenMapSuspended = fullscreen;
                if (fullscreen)
                {
                    // Restore the unmodified game projection immediately.
                    // This also ensures it is unshifted if PiP becomes a render texture.
                    RestoreTrackedProjection();
                    _autoCalculated = false;
                    _autoStatus = "Fullscreen Tactical Display: camera offset temporarily suspended.";
                    Logger.LogInfo("Fullscreen tactical map detected: projection offset suspended.");
                }
                else
                {
                    ResetAutoCenterObservation();
                    _autoStatus = _autoCenterEnabled
                        ? "Auto: recalculating after fullscreen map..."
                        : "Auto-center off (manual offsets active).";
                    Logger.LogInfo("Windowed Tactical Display restored: original offset mode resumed.");
                }
            }
            catch (Exception ex)
            {
                // Detection failure should not crash a mission.
                Logger.LogWarning("Fullscreen Tactical Display detection: " + ex.Message);
            }
        }

        private GameWindow? FindTacticalDisplay()
        {
            try
            {
                if (_lastTacticalWindow != null && _lastTacticalWindow.TryGetTarget(out GameWindow cached)
                    && cached.IsLoaded && IsTacticalDisplay(cached))
                    return cached;

                MainGameView view = MainGameView.Instance;
                if (view == null) return null;
                // Fallback for a map already open when the window hook is installed.
                var queue = new Queue<DependencyObject>();
                queue.Enqueue(view);
                int count = 0;
                while (queue.Count > 0 && count++ < 10000)
                {
                    DependencyObject node = queue.Dequeue();
                    if (node is GameWindow map && map.IsLoaded && IsTacticalDisplay(map))
                    {
                        _lastTacticalWindow = new WeakReference<GameWindow>(map);
                        return map;
                    }
                    int children = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < children; ++i)
                    {
                        DependencyObject child = VisualTreeHelper.GetChild(node, i);
                        if (child != null) queue.Enqueue(child);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Tactical Display scan failed: " + ex.Message);
            }
            return null;
        }

        // Capture the original window once per loaded tactical-window instance.
        // Repeated Apply/Move actions must preserve the first original geometry.
        private void SnapshotOriginalGeometry(GameWindow map)
        {
            if (_testPositionActive && ReferenceEquals(map, _testWindow) &&
                ReferenceEquals(MainGameView.Instance, _testView)) return;

            _testWindow = map;
            _testView = MainGameView.Instance;
            _oldMapX = map.X;
            _oldMapY = map.Y;
            _oldMapWidth = map.Width;  // May be NaN for automatic sizing: preserve as-is.
            _oldMapHeight = map.Height;
            _oldMapAnchorBottom = map.AnchorBottom;
            _oldMapAnchorRight = map.AnchorRight;
            _testPositionActive = true;
        }

        private GameWindow? GetEditableTacticalDisplay()
        {
            if (!_mapHooksInstalled)
            {
                _tacticalStatus = "UI hook not ready. Open a mission, then retry.";
                return null;
            }
            GameWindow? map = FindTacticalDisplay();
            if (map == null)
            {
                _tacticalStatus = "Map window not found. Open Tactical Display.";
                return null;
            }
            if (IsFullscreenTacticalDisplay(map) || map.Maximized)
            {
                _tacticalStatus = "Exit fullscreen/maximized map first.";
                return null;
            }
            return map;
        }

        private void TrackGameWindow(GameWindow window)
        {
            foreach (WeakReference<GameWindow> weak in _observedWindows)
            {
                if (weak.TryGetTarget(out GameWindow known) && ReferenceEquals(known, window))
                    return;
            }
            if (_observedWindows.Count > 256)
                _observedWindows.RemoveAll(w => !w.TryGetTarget(out _));
            _observedWindows.Add(new WeakReference<GameWindow>(window));
        }

        private void SuppressGameWindow(GameWindow window)
        {
            if (!window.IsLoaded) return;
            foreach (WindowHitTestSnapshot entry in _suppressedWindows)
                if (ReferenceEquals(entry.Window, window)) return;

            // Leave originally disabled windows alone; never force them active.
            bool original = window.IsHitTestVisible;
            if (!original) return;
            _suppressedWindows.Add(new WindowHitTestSnapshot(window, original));
            window.IsHitTestVisible = false;
        }

        private void ScanGameWindows(MainGameView? view)
        {
            // Recently initialized floating windows may not yet be attached to the
            // visible visual tree. Keep weak references, never retain old scenes.
            for (int i = _observedWindows.Count - 1; i >= 0; --i)
            {
                if (!_observedWindows[i].TryGetTarget(out GameWindow window))
                {
                    _observedWindows.RemoveAt(i);
                    continue;
                }
                try { SuppressGameWindow(window); }
                catch (Exception ex) { ReportUiGuardWarning(ex); }
            }
            if (view == null) return;

            var queue = new Queue<DependencyObject>();
            queue.Enqueue(view);
            int nodes = 0;
            while (queue.Count != 0 && nodes++ < 10000)
            {
                DependencyObject node = queue.Dequeue();
                try
                {
                    if (node is GameWindow window)
                    {
                        TrackGameWindow(window);
                        SuppressGameWindow(window);
                    }
                    int children = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < children; ++i)
                    {
                        DependencyObject child = VisualTreeHelper.GetChild(node, i);
                        if (child != null) queue.Enqueue(child);
                    }
                }
                catch (Exception ex) { ReportUiGuardWarning(ex); }
            }
        }

        private void ReportUiGuardWarning(Exception ex)
        {
            if (_uiGuardWarningIssued) return;
            _uiGuardWarningIssued = true;
            Logger.LogWarning("Ctrl+Shift+U Noesis input guard: " + ex.Message);
        }

        private void GuardAllUiHitTesting()
        {
            bool needGuard = Application.isFocused &&
                ((_visible && (_mouseDragCaptured || PointerWithinWindow())) ||
                 (_launcherEnabledSetting.Value &&
                  (_launcherMouseCaptured || PointerWithinLauncher())));
            if (!needGuard)
            {
                RestoreAllUiHitTesting();
                return;
            }
            try
            {
                MainGameView? view = MainGameView.Instance;
                UIElement? root = (object?)view as UIElement;
                if (_suppressedUiRoot != null && !ReferenceEquals(root, _suppressedUiRoot))
                    RestoreAllUiHitTesting();

                // Snapshot child windows before blocking the UI root: some Noesis
                // implementations report inherited hit-test state to descendants.
                if (!_uiGuardWasActive || Time.unscaledTime >= _nextUiGuardScan)
                {
                    _nextUiGuardScan = Time.unscaledTime + 0.12f;
                    ScanGameWindows(view);
                }
                if (root != null)
                {
                    if (!ReferenceEquals(root, _suppressedUiRoot))
                    {
                        _suppressedUiRoot = root;
                        _uiRootWasEnabled = root.IsHitTestVisible;
                    }
                    if (root.IsHitTestVisible) root.IsHitTestVisible = false;
                }
                // The game may reset a floating window's flag on reactivation.
                foreach (WindowHitTestSnapshot entry in _suppressedWindows)
                {
                    try
                    {
                        if (entry.Window.IsLoaded && entry.Window.IsHitTestVisible)
                            entry.Window.IsHitTestVisible = false;
                    }
                    catch (Exception ex) { ReportUiGuardWarning(ex); }
                }
                _uiGuardWasActive = true;
            }
            catch (Exception ex) { ReportUiGuardWarning(ex); }
        }

        private void RestoreAllUiHitTesting()
        {
            // Restore the parent first so descendants regain their own state.
            UIElement? root = _suppressedUiRoot;
            _suppressedUiRoot = null;
            if (root != null)
            {
                try { root.IsHitTestVisible = _uiRootWasEnabled; }
                catch (Exception ex) { ReportUiGuardWarning(ex); }
            }
            foreach (WindowHitTestSnapshot entry in _suppressedWindows)
            {
                // Also restore unloaded windows: Sea Power may reuse them later.
                try { entry.Window.IsHitTestVisible = entry.WasEnabled; }
                catch (Exception ex) { ReportUiGuardWarning(ex); }
            }
            _suppressedWindows.Clear();
            _uiGuardWasActive = false;
            _nextUiGuardScan = 0f;
        }

        private static bool ValidNoesisDimension(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        // Match the game's UI size constraints; never exceed the visible screen.
        private static bool ResolveMapSize(GameWindow map, float requestedW, float requestedH,
            float availableW, float availableH, out float width, out float height)
        {
            width = Mathf.Min(requestedW, availableW);
            height = Mathf.Min(requestedH, availableH);
            if (ValidNoesisDimension(map.MinWidth)) width = Mathf.Max(width, map.MinWidth);
            if (ValidNoesisDimension(map.MinHeight)) height = Mathf.Max(height, map.MinHeight);
            if (ValidNoesisDimension(map.MaxWidth)) width = Mathf.Min(width, map.MaxWidth);
            if (ValidNoesisDimension(map.MaxHeight)) height = Mathf.Min(height, map.MaxHeight);
            return ValidNoesisDimension(width) && ValidNoesisDimension(height) &&
                width <= availableW + 0.01f && height <= availableH + 0.01f;
        }

        private void ApplySplitLayout(SplitLayout layout)
        {
            _pendingLayoutAutoTrim = false;
            try
            {
                GameWindow? map = GetEditableTacticalDisplay();
                if (map == null) return;
                float scale = GetUiScale();
                float usableW = Screen.width / scale;
                float usableH = Screen.height / scale;
                bool isSideBySide = layout == SplitLayout.MapLeft || layout == SplitLayout.MapRight;
                bool isQuarter = layout == SplitLayout.MapTopLeft || layout == SplitLayout.MapBottomLeft ||
                                 layout == SplitLayout.MapTopRight || layout == SplitLayout.MapBottomRight;
                float requestedW = usableW * (isSideBySide || isQuarter ? 0.5f : 1f);
                float requestedH = usableH * (isSideBySide ? 1f : 0.5f);
                if (!ResolveMapSize(map, requestedW, requestedH, usableW, usableH,
                    out float width, out float height))
                {
                    _tacticalStatus = "The game's map size limits exceed the viewport.";
                    return;
                }

                bool onRight = layout == SplitLayout.MapRight || layout == SplitLayout.MapTopRight ||
                               layout == SplitLayout.MapBottomRight;
                bool onBottom = layout == SplitLayout.MapBottom || layout == SplitLayout.MapBottomLeft ||
                                layout == SplitLayout.MapBottomRight;
                float x = onRight ? usableW - width : 0f;
                float y = onBottom ? usableH - height : 0f;
                // Half-screen modes retain their already-tested offsets. Quarter
                // modes center the vessel on the CENTROID of the uncovered L-shaped
                // 75% of the screen, not just one arbitrarily chosen half.
                float offsetX = layout == SplitLayout.MapLeft ? width / (2f * usableW) :
                                layout == SplitLayout.MapRight ? -width / (2f * usableW) : 0f;
                float offsetY = layout == SplitLayout.MapTop ? -height / (2f * usableH) :
                                layout == SplitLayout.MapBottom ? height / (2f * usableH) : 0f;
                if (isQuarter)
                {
                    float covered = width * height / (usableW * usableH);
                    float free = Mathf.Max(0.001f, 1f - covered);
                    float coveredX = (x + width * 0.5f) / usableW;
                    float coveredY = (y + height * 0.5f) / usableH;
                    offsetX = Mathf.Clamp((0.5f - covered * coveredX) / free - 0.5f,
                        -MaximumOffset, MaximumOffset);
                    offsetY = Mathf.Clamp(0.5f - (0.5f - covered * coveredY) / free,
                        -MaximumOffset, MaximumOffset);
                }

                SnapshotOriginalGeometry(map);
                // Only record the previous camera view once, allowing users to
                // switch among split layouts and still restore their prior view.
                if (!_splitLayoutActive)
                {
                    _cameraBeforeSplitX = _horizontal;
                    _cameraBeforeSplitY = _vertical;
                    _cameraBeforeSplitEnabled = _enabled;
                }
                // Preserve the user's chosen Auto-center mode. If enabled, the
                // layout only changes map geometry and the auto controller uses
                // the actual remaining 3D area. Manual offsets must not be
                // overwritten by selecting a split while Auto-center is ON.
                bool keepAutoCenter = _autoCenterEnabled;
                if (keepAutoCenter)
                {
                    ResetAutoCenterObservation();
                    _autoTargetX = offsetX;
                    _autoTargetY = offsetY;
                    _autoCalculated = true; // Immediate provisional layout center.
                    _autoStatus = "Auto: verifying split geometry...";
                }
                else
                    _autoStatus = "Split layout applied (manual camera offset).";
                map.AnchorBottom = false;
                map.AnchorRight = false;
                map.Width = width;
                map.Height = height;
                map.X = x;
                map.Y = y;
                _splitLayoutActive = true;
                _enabled = true;
                if (!keepAutoCenter)
                    ChangeOffset(offsetX, offsetY);
                _pendingSave = true;
                SaveSettings();
                string label = layout == SplitLayout.MapLeft ? "map left / 3D right" :
                               layout == SplitLayout.MapRight ? "map right / 3D left" :
                               layout == SplitLayout.MapTop ? "map top / 3D bottom" :
                               layout == SplitLayout.MapBottom ? "map bottom / 3D top" :
                               layout == SplitLayout.MapTopLeft ? "25% map top-left" :
                               layout == SplitLayout.MapBottomLeft ? "25% map bottom-left" :
                               layout == SplitLayout.MapTopRight ? "25% map top-right" :
                               "25% map bottom-right";
                _tacticalStatus = "Applied " + label + ". Restore reverses it.";
                Logger.LogInfo("Split layout " + label + $": map {width:0.#}x{height:0.#} " +
                    $"at ({x:0.#},{y:0.#}); camera ({offsetX:0.##},{offsetY:0.##}).");
            }
            catch (Exception ex)
            {
                _tacticalStatus = "Split layout failed. Use Restore; see log.";
                Logger.LogWarning("Tactical split layout: " + ex);
            }
        }

        private void RestoreMapAndSplitLayout()
        {
            _pendingLayoutAutoTrim = false;
            bool hadOriginal = _testPositionActive;
            RestoreTacticalDisplayPosition();
            if (!_splitLayoutActive) return;
            _splitLayoutActive = false;
            // Do not revert Auto-center: the user may have changed it explicitly.
            ResetAutoCenterObservation();
            _autoStatus = _autoCenterEnabled ? "Auto: observing Tactical Display geometry..." :
                "Auto-center off (manual offsets active).";
            _enabled = _cameraBeforeSplitEnabled;
            ChangeOffset(_cameraBeforeSplitX, _cameraBeforeSplitY);
            if (!_enabled) RestoreTrackedProjection();
            _pendingSave = true;
            SaveSettings();
            _tacticalStatus = hadOriginal ? "Restored map and pre-split camera view." :
                "Restored camera view; map window no longer available.";
        }

        private static float GetUiScale()
        {
            float uiScale = (Singleton<OptionsManager>.InstanceExists()
                ? Singleton<OptionsManager>.Instance.UIScale : 1f);
            return Mathf.Max(0.01f, uiScale * Screen.height / 1080f);
        }

        private void MoveTacticalDisplayUpperRight()
        {
            try
            {
                GameWindow? map = GetEditableTacticalDisplay();
                if (map == null) return;
                float scale = GetUiScale();
                float usableWidth = Screen.width / scale;
                float width = float.IsNaN(map.Width) ? map.ActualWidth : map.Width;
                if (width <= 0f || float.IsNaN(width) || float.IsInfinity(width))
                {
                    _tacticalStatus = "Map has no valid width yet. Retry after opening.";
                    return;
                }
                SnapshotOriginalGeometry(map);
                map.AnchorBottom = false;
                map.AnchorRight = false;
                map.X = Mathf.Max(0f, usableWidth - width - 12f);
                map.Y = 12f;
                _tacticalStatus = "Position changed only. Restore undoes it.";
                Logger.LogInfo($"Tactical Display moved to ({map.X:0.#},{map.Y:0.#}); " +
                    $"size unchanged, UI scale {scale:0.###}.");
            }
            catch (Exception ex)
            {
                _tacticalStatus = "Map move failed; see BepInEx log.";
                Logger.LogWarning("Tactical Display move: " + ex);
            }
        }

        private void ResizeTacticalDisplayUpperRight()
        {
            try
            {
                GameWindow? map = GetEditableTacticalDisplay();
                if (map == null) return;

                float scale = GetUiScale();
                float usableWidth = Screen.width / scale;
                float usableHeight = Screen.height / scale;
                // A 100% request must truly reach the screen edge. The earlier
                // 12px margin is only used when that dimension is below 100%.
                float marginX = _mapWidthFraction >= 0.999f ? 0f : 12f;
                float marginY = _mapHeightFraction >= 0.999f ? 0f : 12f;
                if (usableWidth <= marginX || usableHeight <= marginY)
                {
                    _tacticalStatus = "Viewport too small for the chosen map size.";
                    return;
                }
                float requestedW = usableWidth * _mapWidthFraction;
                float requestedH = usableHeight * _mapHeightFraction;
                if (!ResolveMapSize(map, requestedW, requestedH,
                    usableWidth - marginX, usableHeight - marginY,
                    out float width, out float height))
                {
                    _tacticalStatus = "Map minimum size exceeds available screen space.";
                    return;
                }

                SnapshotOriginalGeometry(map);
                // Apply dimensions before the corner position. Some window hosts
                // recompute their anchor offsets when the size changes.
                map.AnchorBottom = false;
                map.AnchorRight = false;
                map.Width = width;
                map.Height = height;
                map.X = Mathf.Max(0f, usableWidth - width - marginX);
                map.Y = marginY;
                _tacticalStatus = $"Map {Mathf.RoundToInt(width / usableWidth * 100f)}% x " +
                    $"{Mathf.RoundToInt(height / usableHeight * 100f)}% (game limits apply).";
                Logger.LogInfo($"Tactical Display resized to ({width:0.#},{height:0.#}) " +
                    $"and moved to ({map.X:0.#},{map.Y:0.#}), UI scale {scale:0.###}.");
            }
            catch (Exception ex)
            {
                _tacticalStatus = "Resize failed; see BepInEx log. Use Restore.";
                Logger.LogWarning("Tactical Display resize: " + ex);
            }
        }

        private void RestoreTacticalDisplayPosition()
        {
            if (!_testPositionActive || _testWindow == null)
            {
                _tacticalStatus = "No original map geometry captured yet.";
                return;
            }
            try
            {
                if (!ReferenceEquals(MainGameView.Instance, _testView) || !_testWindow.IsLoaded)
                {
                    _tacticalStatus = "Previous map closed; no window to restore.";
                    _testWindow = null;
                    _testView = null;
                    _testPositionActive = false;
                    return;
                }
                _testWindow.AnchorBottom = false;
                _testWindow.AnchorRight = false;
                _testWindow.Width = _oldMapWidth;
                _testWindow.Height = _oldMapHeight;
                _testWindow.X = _oldMapX;
                _testWindow.Y = _oldMapY;
                _testWindow.AnchorBottom = _oldMapAnchorBottom;
                _testWindow.AnchorRight = _oldMapAnchorRight;
                _testPositionActive = false;
                _testWindow = null;
                _testView = null;
                _tacticalStatus = "Original map position AND size restored.";
                Logger.LogInfo("Tactical Display original geometry restored.");
            }
            catch (Exception ex)
            {
                _tacticalStatus = "Restore failed; see BepInEx log.";
                Logger.LogWarning("Tactical Display restore: " + ex);
            }
        }

        private void OnApplicationQuit()
        {
            // This is a temporary, non-persistent position test. Where possible,
            // return the window before the game serializes any remembered layout.
            RestoreAllUiHitTesting();
            if (_testPositionActive || _splitLayoutActive) RestoreMapAndSplitLayout();
        }
    }
}

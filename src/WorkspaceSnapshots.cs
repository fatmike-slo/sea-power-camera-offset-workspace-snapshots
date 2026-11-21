using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Configuration;
using Noesis;
using UnityEngine;
using GUI = UnityEngine.GUI;
using GameWindow = SeapowerUI.Window;
using Rect = UnityEngine.Rect;

namespace CustomCameraOffset
{
    // Camera Offset & Workspace Snapshots WIP4: four opt-in whole-screen snapshots, including the tactical
    // map, camera mode/framing, and other loaded in-game floating windows.
    // A snapshot is applied ONCE per window instance; subsequent user dragging
    // is never counteracted. Known panels may be shown/hidden if initialized.
    public sealed partial class Plugin
    {
        private sealed class WorkspaceWindow
        {
            public string Key = "";
            public Rect Bounds;
            public bool Visible = true;
        }

        private sealed class WorkspaceSnapshot
        {
            // Legacy flag retained for reading prior WIP2 snapshots. Quick map
            // layouts never use snapshots and never touch other windows.
            public bool KeepCurrentCamera;
            public bool HasMap;
            public Rect Map;
            public bool CameraEnabled;
            public bool AutoCenter;
            public float CameraX;
            public float CameraY;
            public readonly List<WorkspaceWindow> Windows = new List<WorkspaceWindow>();
        }

        private readonly ConfigEntry<bool>[] _workspaceSaved = new ConfigEntry<bool>[4];
        private readonly ConfigEntry<string>[] _workspaceNames = new ConfigEntry<string>[4];
        private readonly ConfigEntry<string>[] _workspaceValues = new ConfigEntry<string>[4];
        private readonly string[] _workspaceDrafts = new string[4];
        private ConfigEntry<bool> _workspaceAutoRestoreSetting = null!;
        private ConfigEntry<int> _workspaceStartupSlotSetting = null!;
        private bool _workspacePage;
        private string _workspaceMessage = "Arrange windows in a mission, then Save a snapshot.";
        private WorkspaceSnapshot? _activeWorkspace;
        private int _activeWorkspaceSlot = -1;
        private readonly List<WeakReference<GameWindow>> _workspaceApplied = new List<WeakReference<GameWindow>>();
        private bool _workspaceAppliedMap;
        private bool _workspaceCameraApplied;
        private float _nextWorkspaceProbe;

        private void BindWorkspaceConfig()
        {
            const string section = "Workspace Snapshots";
            _workspaceAutoRestoreSetting = Config.Bind(section, "AutoRestore", false,
                "When enabled, restore the selected workspace after a new mission loads. Off by default.");
            _workspaceStartupSlotSetting = Config.Bind(section, "StartupSlot", 1,
                "Workspace snapshot to restore at mission startup (1..4). Visibility supported for initialized windows.");
            for (int i = 0; i < 4; i++)
            {
                string prefix = "Slot" + (i + 1);
                _workspaceSaved[i] = Config.Bind(section, prefix + "Saved", false,
                    "Whether this complete workspace snapshot exists.");
                _workspaceNames[i] = Config.Bind(section, prefix + "Name", "Workspace " + (i + 1),
                    "Display name for this workspace snapshot.");
                _workspaceValues[i] = Config.Bind(section, prefix + "Data", "",
                    "Base64 snapshot of map, camera and game windows (V1 or V2). Do not edit by hand.");
                _workspaceDrafts[i] = CleanLayoutName(_workspaceNames[i].Value, i);
            }
        }

        private static string FloatText(float f) => f.ToString("R", CultureInfo.InvariantCulture);
        private static bool ReadFloat(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value);
        private static string RectText(Rect r) => string.Join(",", new[] {
            FloatText(r.x), FloatText(r.y), FloatText(r.width), FloatText(r.height) });
        private static bool ReadRect(string text, out Rect rect)
        {
            rect = new Rect();
            string[] a = text.Split(',');
            if (a.Length != 4 || !ReadFloat(a[0], out float x) || !ReadFloat(a[1], out float y) ||
                !ReadFloat(a[2], out float w) || !ReadFloat(a[3], out float h)) return false;
            if (x < -0.01f || y < -0.01f || w < 0.03f || h < 0.03f ||
                x > 1.01f || y > 1.01f || w > 1.01f || h > 1.01f) return false;
            rect = new Rect(x, y, w, h);
            return true;
        }

        // Raw record is Base64-encoded to keep BepInEx configuration lines safe.
        // No additional runtime JSON package or Unity/game DLL is required.
        private static string EncodeWorkspace(WorkspaceSnapshot snap)
        {
            var sb = new StringBuilder("V2|");
            sb.Append(snap.HasMap ? RectText(snap.Map) : "-").Append('|');
            sb.Append(snap.CameraEnabled ? "1" : "0").Append(',');
            sb.Append(snap.AutoCenter ? "1" : "0").Append(',');
            sb.Append(FloatText(snap.CameraX)).Append(',').Append(FloatText(snap.CameraY));
            foreach (WorkspaceWindow w in snap.Windows)
            {
                sb.Append('|').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(w.Key)));
                sb.Append(';').Append(RectText(w.Bounds)).Append(';').Append(w.Visible ? "1" : "0");
            }
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static bool TryDecodeWorkspace(string data, out WorkspaceSnapshot snap)
        {
            snap = new WorkspaceSnapshot();
            try
            {
                if (string.IsNullOrEmpty(data) || data.Length > 24000) return false;
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(data)).Split('|');
                if (parts.Length < 3 || parts.Length > 48 || (parts[0] != "V1" && parts[0] != "V2")) return false;
                if (parts[1] != "-")
                {
                    if (!ReadRect(parts[1], out Rect map)) return false;
                    snap.HasMap = true;
                    snap.Map = map;
                }
                string[] camera = parts[2].Split(',');
                if (camera.Length != 4 || (camera[0] != "0" && camera[0] != "1") ||
                    (camera[1] != "0" && camera[1] != "1") ||
                    !ReadFloat(camera[2], out snap.CameraX) ||
                    !ReadFloat(camera[3], out snap.CameraY) ||
                    Math.Abs(snap.CameraX) > MaximumOffset || Math.Abs(snap.CameraY) > MaximumOffset)
                    return false;
                snap.CameraEnabled = camera[0] == "1";
                snap.AutoCenter = camera[1] == "1";
                var keys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 3; i < parts.Length; i++)
                {
                    string[] row = parts[i].Split(';');
                    if ((row.Length != 2 && row.Length != 3) || !ReadRect(row[1], out Rect bounds) ||
                        (row.Length == 3 && row[2] != "0" && row[2] != "1")) return false;
                    string key = Encoding.UTF8.GetString(Convert.FromBase64String(row[0]));
                    if (key.Length < 3 || key.Length > 240 || !keys.Add(key)) return false;
                    snap.Windows.Add(new WorkspaceWindow { Key = key, Bounds = bounds,
                        Visible = row.Length == 2 || row[2] == "1" });
                }
                return true;
            }
            catch { return false; }
        }

        private static string? WorkspaceKey(GameWindow window)
        {
            if (IsTacticalDisplay(window)) return null; // Stored as a separate map record.
            object? context = MapContext(window);
            string type = context?.GetType().FullName ?? "";
            string header = HeaderText(window.Header).Trim();
            string check = type + " " + header;
            string normalized = header.ToLowerInvariant();
            if (normalized.StartsWith("formation manager")) return "panel:formation";
            if (normalized.StartsWith("event log")) return "panel:eventlog";
            if (normalized.StartsWith("unit status")) return "panel:status";
            if (normalized.StartsWith("unit reference")) return "panel:reference";
            if (normalized.StartsWith("camera controls")) return "panel:camera";
            // Transient dialogs and menus are not workspace members.
            foreach (string exclusion in new[] { "Popup", "Dialog", "Tooltip", "Notification", "ContextMenu", "Confirmation" })
                if (check.IndexOf(exclusion, StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (type.Length > 3 && type.Length <= 220 && type.IndexOf("ViewModel", StringComparison.OrdinalIgnoreCase) >= 0)
                return "vm:" + type;
            if (header.Length > 1 && header.Length < 120)
                return "title:" + header.ToLowerInvariant();
            return null;
        }

        // Support historical V1 keys saved before panel-role normalization.
        private static string? LegacyWorkspaceKey(GameWindow window)
        {
            if (IsTacticalDisplay(window)) return null;
            object? context = MapContext(window);
            string type = context?.GetType().FullName ?? "";
            string header = HeaderText(window.Header).Trim();
            if (type.Length > 3 && type.Length <= 220 &&
                type.IndexOf("ViewModel", StringComparison.OrdinalIgnoreCase) >= 0)
                return "vm:" + type;
            if (header.Length > 1 && header.Length < 120)
                return "title:" + header.ToLowerInvariant();
            return null;
        }

        private GameWindow? FindUniqueLegacyWorkspaceWindow(string key)
        {
            GameWindow? candidate = null;
            foreach (GameWindow window in CurrentWorkspaceWindows(true))
            {
                if (LegacyWorkspaceKey(window) != key) continue;
                if (candidate != null) return null;
                candidate = window;
            }
            return candidate;
        }

        private List<GameWindow> CurrentWorkspaceWindows(bool includeHidden = false)
        {
            var windows = new List<GameWindow>();
            var view = SeapowerUI.MainGameView.Instance;
            if (view == null) return windows;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(view);
            int visited = 0;
            while (queue.Count > 0 && visited++ < 10000)
            {
                DependencyObject node = queue.Dequeue();
                try
                {
                    if (node is GameWindow w && w.IsLoaded && (includeHidden || w.Visibility == Visibility.Visible) && !w.Maximized)
                        windows.Add(w);
                    int children = VisualTreeHelper.GetChildrenCount(node);
                    for (int i = 0; i < children; i++)
                    {
                        DependencyObject child = VisualTreeHelper.GetChild(node, i);
                        if (child != null) queue.Enqueue(child);
                    }
                }
                catch { /* one transient UI branch must not block other windows */ }
            }
            // The hook also observes windows that have temporarily left the visual tree.
            if (includeHidden)
            {
                foreach (WeakReference<GameWindow> weak in _observedWindows)
                {
                    if (!weak.TryGetTarget(out GameWindow w) || !w.IsLoaded || w.Maximized || windows.Contains(w)) continue;
                    windows.Add(w);
                }
            }
            return windows;
        }

        // Window identity must be unique among currently loaded windows: never
        // guess which of two identical ViewModels belongs to a saved placement.
        private static Dictionary<string, GameWindow> UniqueWorkspaceWindows(List<GameWindow> windows)
        {
            var found = new Dictionary<string, GameWindow>(StringComparer.Ordinal);
            var duplicated = new HashSet<string>(StringComparer.Ordinal);
            foreach (GameWindow w in windows)
            {
                string? key = WorkspaceKey(w);
                if (key == null) continue;
                if (found.ContainsKey(key)) duplicated.Add(key);
                else found[key] = w;
            }
            foreach (string duplicate in duplicated) found.Remove(duplicate);
            return found;
        }

        private void SaveWorkspace(int index)
        {
            if (!_missionActive) { _workspaceMessage = "Start a 3D mission before saving."; return; }
            GameWindow? map = FindTacticalDisplay();
            var snapshot = new WorkspaceSnapshot();
            if (map != null && map.IsLoaded && map.Visibility == Visibility.Visible)
            {
                if (!TryGetMapGeometry(map, out Rect bounds))
                { _workspaceMessage = "Exit fullscreen/map resizing before saving."; return; }
                snapshot.HasMap = true;
                snapshot.Map = bounds;
                if (_autoCenterEnabled && !_autoCalculated)
                { _workspaceMessage = "Allow Auto-center to settle, then Save."; return; }
            }
            snapshot.CameraEnabled = _enabled;
            snapshot.AutoCenter = _autoCenterEnabled;
            snapshot.CameraX = _enabled ? EffectiveHorizontal : 0f;
            snapshot.CameraY = _enabled ? EffectiveVertical : 0f;
            foreach (KeyValuePair<string, GameWindow> item in UniqueWorkspaceWindows(CurrentWorkspaceWindows(true)))
            {
                if (snapshot.Windows.Count >= 32) break;
                bool visible = item.Value.Visibility == Visibility.Visible;
                if (!TryGetMapGeometry(item.Value, out Rect bounds))
                {
                    if (visible) continue;
                    bounds = new Rect(.65f, .73f, .32f, .25f);
                }
                snapshot.Windows.Add(new WorkspaceWindow { Key = item.Key, Bounds = bounds, Visible = visible });
            }
            // A known panel not created yet is considered closed in this snapshot.
            foreach (string known in ManagedPanelKeys)
                if (!snapshot.Windows.Exists(w => w.Key == known))
                    snapshot.Windows.Add(new WorkspaceWindow { Key = known,
                        Visible = false, Bounds = new Rect(.65f, .73f, .32f, .25f) });
            // The saved live arrangement is now authoritative. Do not leave a
            // different snapshot armed for windows opened later this mission.
            _activeWorkspace = null;
            _activeWorkspaceSlot = -1;
            _workspaceApplied.Clear();
            _workspaceAppliedMap = false;
            _workspaceCameraApplied = false;
            _workspaceValues[index].Value = EncodeWorkspace(snapshot);
            string name = CleanLayoutName(_workspaceDrafts[index], index);
            _workspaceNames[index].Value = name;
            _workspaceDrafts[index] = name;
            _workspaceSaved[index].Value = true;
            _workspaceMessage = "Saved '" + name + "': map " + (snapshot.HasMap ? "yes" : "no") +
                ", plus " + snapshot.Windows.Count + " panel records and camera.";
            SaveSettings();
            Logger.LogInfo("Camera Offset & Workspace Snapshots workspace saved: slot " + (index + 1) + ", " + snapshot.Windows.Count + " additional windows.");
        }

        private void StartWorkspaceRestore(int index, bool automatic)
        {
            if (!_workspaceSaved[index].Value || !TryDecodeWorkspace(_workspaceValues[index].Value, out WorkspaceSnapshot snap))
            {
                _workspaceMessage = "Workspace " + (index + 1) + " missing or invalid; nothing applied.";
                return;
            }
            ArmWorkspace(snap, index, automatic);
            _workspaceMessage = (automatic ? "Auto-restoring " : "Restoring ") +
                "'" + _workspaceNames[index].Value + "' as windows appear.";
            Logger.LogInfo("Camera Offset & Workspace Snapshots workspace restore armed: slot " + (index + 1) +
                ", " + snap.Windows.Count + " windows; auto=" + automatic + ".");
        }

        private void ArmWorkspace(WorkspaceSnapshot snap, int slot, bool automatic)
        {
            _workspaceApplied.Clear();
            _workspaceAppliedMap = false;
            _workspaceCameraApplied = false;
            _activeWorkspace = snap;
            _activeWorkspaceSlot = slot;
            _nextWorkspaceProbe = Time.unscaledTime + (automatic ? 1.0f : 0f);
        }

        private static bool IsAlreadyApplied(List<WeakReference<GameWindow>> applied, GameWindow w)
        {
            foreach (WeakReference<GameWindow> weak in applied)
                if (weak.TryGetTarget(out GameWindow known) && ReferenceEquals(w, known)) return true;
            return false;
        }

        private static bool ApplyWorkspaceRect(GameWindow w, Rect bounds)
        {
            if (!w.IsLoaded || w.Maximized || w.Visibility != Visibility.Visible) return false;
            float scale = GetUiScale();
            float viewportW = Screen.width / scale;
            float viewportH = Screen.height / scale;
            if (!ValidNoesisDimension(viewportW) || !ValidNoesisDimension(viewportH)) return false;
            if (!ResolveMapSize(w, bounds.width * viewportW, bounds.height * viewportH,
                    viewportW, viewportH, out float width, out float height)) return false;
            float x = Mathf.Clamp(bounds.x * viewportW, 0f, Mathf.Max(0f, viewportW - width));
            float y = Mathf.Clamp(bounds.y * viewportH, 0f, Mathf.Max(0f, viewportH - height));
            // Same proven order as original map layouts. No change to Noesis content.
            w.AnchorBottom = false;
            w.AnchorRight = false;
            w.Width = width;
            w.Height = height;
            w.X = x;
            w.Y = y;
            return true;
        }

        private static bool ApplyWorkspacePanel(WorkspaceWindow saved, GameWindow window)
        {
            if (!window.IsLoaded || window.Maximized) return false;
            if (!saved.Visible)
            {
                // A reversible hide, never game Close()/Destroy(); a future
                // workspace can re-show this same Noesis window instance.
                if (window.Visibility == Visibility.Visible)
                    window.Visibility = Visibility.Hidden;
                return true;
            }
            if (window.Visibility != Visibility.Visible)
                window.Visibility = Visibility.Visible;
            return ApplyWorkspaceRect(window, saved.Bounds);
        }

        private void ApplyWorkspaceCamera(WorkspaceSnapshot snapshot)
        {
            if (snapshot.KeepCurrentCamera)
            {
                ResetAutoCenterObservation();
                return;
            }
            SetAutoCenterEnabled(snapshot.AutoCenter);
            _enabled = snapshot.CameraEnabled;
            ChangeOffset(snapshot.CameraX, snapshot.CameraY);
            _pendingLayoutAutoTrim = false;
            if (_autoCenterEnabled && _enabled && snapshot.HasMap)
            {
                ChangeAutoTrim(0f, 0f);
                ResetAutoCenterObservation();
                _pendingLayoutCameraX = snapshot.CameraX;
                _pendingLayoutCameraY = snapshot.CameraY;
                _pendingLayoutAutoTrim = true;
            }
            else
            {
                ResetAutoCenterObservation();
                if (!_enabled) RestoreTrackedProjection();
            }
            _pendingSave = true;
            SaveSettings();
        }

        private void PollWorkspaceRestoration()
        {
            if (!_missionActive || _activeWorkspace == null ||
                Time.unscaledTime < _nextWorkspaceProbe || !_mapHooksInstalled) return;
            _nextWorkspaceProbe = Time.unscaledTime + 0.40f;
            WorkspaceSnapshot snap = _activeWorkspace;
            if (snap.HasMap && !_workspaceAppliedMap)
            {
                try
                {
                    GameWindow? map = FindTacticalDisplay();
                    if (map != null && TryGetMapGeometry(map, out Rect ignored))
                    {
                        SnapshotOriginalGeometry(map);
                        if (!_splitLayoutActive)
                        {
                            _cameraBeforeSplitX = _horizontal;
                            _cameraBeforeSplitY = _vertical;
                            _cameraBeforeSplitEnabled = _enabled;
                        }
                        if (ApplyWorkspaceRect(map, snap.Map))
                        {
                            _splitLayoutActive = true;
                            _workspaceAppliedMap = true;
                            ResetAutoCenterObservation();
                        }
                    }
                }
                catch (Exception ex) { Logger.LogWarning("Camera Offset & Workspace Snapshots map restore: " + ex.Message); }
            }
            if (!_workspaceCameraApplied && (!snap.HasMap || _workspaceAppliedMap))
            {
                try { ApplyWorkspaceCamera(snap); _workspaceCameraApplied = true; }
                catch (Exception ex) { Logger.LogWarning("Camera Offset & Workspace Snapshots camera restore: " + ex.Message); }
            }
            var active = UniqueWorkspaceWindows(CurrentWorkspaceWindows(true));
            foreach (WorkspaceWindow saved in snap.Windows)
            {
                if (!active.TryGetValue(saved.Key, out GameWindow w))
                {
                    if (!saved.Key.StartsWith("vm:", StringComparison.Ordinal) &&
                        !saved.Key.StartsWith("title:", StringComparison.Ordinal)) continue;
                    GameWindow? legacy = FindUniqueLegacyWorkspaceWindow(saved.Key);
                    if (legacy == null) continue;
                    w = legacy;
                }
                if (IsAlreadyApplied(_workspaceApplied, w)) continue;
                try
                {
                    if (saved.Visible && !TryGetMapGeometry(w, out Rect ignored)) continue;
                    if (!ApplyWorkspacePanel(saved, w)) continue;
                    _workspaceApplied.Add(new WeakReference<GameWindow>(w));
                    Logger.LogInfo("Camera Offset & Workspace Snapshots restored panel " + saved.Key + " visible=" + saved.Visible);
                }
                catch (Exception ex) { Logger.LogWarning("Camera Offset & Workspace Snapshots window restore: " + ex.Message); }
            }
            // Unavailable windows stay pending, but there is no continuous
            // write to already-restored windows. Users may freely move them.
        }

        private void WorkspaceMissionReady()
        {
            ClearWorkspaceRuntimeState();
            if (!_workspaceAutoRestoreSetting.Value) return;
            int index = _workspaceStartupSlotSetting.Value - 1;
            if (index >= 0 && index < 4)
            {
                StartWorkspaceRestore(index, true);
                // Allow the game UI and host windows to finish initial layout.
                _nextWorkspaceProbe = Time.unscaledTime + 1.0f;
            }
        }
        private void WorkspaceMissionEnding() => ClearWorkspaceRuntimeState();
        private void ClearWorkspaceRuntimeState()
        {
            _activeWorkspace = null;
            _activeWorkspaceSlot = -1;
            _workspaceApplied.Clear();
            _workspaceAppliedMap = false;
            _workspaceCameraApplied = false;
            _nextWorkspaceProbe = 0f;
            _workspacePage = false;
        }

        private void DrawInlineWorkspaces(float footerY)
        {
            float viewportHeight = Mathf.Max(45f, footerY - 441f);
            Rect viewport = new Rect(22f, 432f, 398f, viewportHeight);
            _workspaceScroll = GUI.BeginScrollView(viewport, _workspaceScroll,
                new Rect(0f, 0f, 382f, 248f), false, true);
            bool auto = GUI.Toggle(new Rect(0f, 1f, 267f, 21f),
                _workspaceAutoRestoreSetting.Value, "Auto-restore selected slot next mission");
            if (auto != _workspaceAutoRestoreSetting.Value)
            {
                _workspaceAutoRestoreSetting.Value = auto;
                SaveSettings();
            }
            if (GUI.Button(new Rect(277f, 0f, 103f, 23f), "Show panels"))
                ShowAllManagedPanels();

            for (int i = 0; i < 4; i++)
            {
                float x = i % 2 == 0 ? 0f : 193f;
                float y = i < 2 ? 29f : 137f;
                GUI.Box(new Rect(x, y, 188f, 103f), "");
                GUI.Label(new Rect(x + 7f, y + 6f, 22f, 20f), (i + 1) + ".");
                _workspaceDrafts[i] = GUI.TextField(new Rect(x + 30f, y + 5f, 150f, 22f),
                    _workspaceDrafts[i], 24);
                if (GUI.Button(new Rect(x + 7f, y + 33f, 83f, 24f), "Save")) SaveWorkspace(i);
                bool enabledBefore = GUI.enabled;
                GUI.enabled = enabledBefore && _workspaceSaved[i].Value;
                if (GUI.Button(new Rect(x + 96f, y + 33f, 84f, 24f), "Load"))
                    StartWorkspaceRestore(i, false);
                GUI.enabled = enabledBefore;
                if (GUI.Button(new Rect(x + 7f, y + 64f, 83f, 24f), "Rename"))
                {
                    string name = CleanLayoutName(_workspaceDrafts[i], i);
                    _workspaceDrafts[i] = name;
                    _workspaceNames[i].Value = name;
                    _workspaceMessage = "Renamed workspace " + (i + 1) + ".";
                    SaveSettings();
                }
                GUI.enabled = enabledBefore && _workspaceSaved[i].Value;
                if (GUI.Button(new Rect(x + 96f, y + 64f, 84f, 24f),
                    _workspaceStartupSlotSetting.Value == i + 1 ? "Startup *" : "Set startup"))
                {
                    _workspaceStartupSlotSetting.Value = i + 1;
                    _workspaceMessage = "Startup workspace: '" + _workspaceNames[i].Value + "'.";
                    SaveSettings();
                }
                GUI.enabled = enabledBefore;
            }
            GUI.EndScrollView();
        }


    }
}

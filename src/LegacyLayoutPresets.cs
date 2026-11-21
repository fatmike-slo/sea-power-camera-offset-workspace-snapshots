using System;
using BepInEx.Configuration;
using UnityEngine;
using GameWindow = SeapowerUI.Window;

namespace CustomCameraOffset
{
    // A layout is the combination of the current Tactical Display rectangle and
    // the current visible 3D framing. Data is expressed in normalized screen
    // coordinates, so stored layouts survive monitor-resolution changes.
    public sealed partial class Plugin
    {
        private sealed class LayoutPreset
        {
            public readonly ConfigEntry<bool> IsSaved;
            public readonly ConfigEntry<string> Name;
            public readonly ConfigEntry<float> Left;
            public readonly ConfigEntry<float> Top;
            public readonly ConfigEntry<float> Width;
            public readonly ConfigEntry<float> Height;
            public readonly ConfigEntry<float> CameraX;
            public readonly ConfigEntry<float> CameraY;
            public readonly ConfigEntry<bool> CameraEnabled;

            public LayoutPreset(ConfigFile config, int number)
            {
                string key = "Slot" + number;
                const string section = "Saved Layouts";
                IsSaved = config.Bind(section, key + "Saved", false, "Whether this combined map and camera layout has been saved.");
                Name = config.Bind(section, key + "Name", "Layout " + number, "User-editable layout name.");
                Left = config.Bind(section, key + "MapLeft", 0f, "Normalized map left edge (0..1).");
                Top = config.Bind(section, key + "MapTop", 0f, "Normalized map top edge (0..1).");
                Width = config.Bind(section, key + "MapWidth", 0.5f, "Normalized map width (0..1).");
                Height = config.Bind(section, key + "MapHeight", 0.5f, "Normalized map height (0..1).");
                CameraX = config.Bind(section, key + "CameraHorizontal", 0f, "Visible horizontal projection shift.");
                CameraY = config.Bind(section, key + "CameraVertical", 0f, "Visible vertical projection shift.");
                CameraEnabled = config.Bind(section, key + "CameraEnabled", true, "Whether camera offset was enabled when saved.");
            }
        }

        private readonly LayoutPreset[] _layoutPresets = new LayoutPreset[4];
        private readonly string[] _layoutNameDrafts = new string[4];
        private bool _pendingLayoutAutoTrim;
        private float _pendingLayoutCameraX;
        private float _pendingLayoutCameraY;

        private static string CleanLayoutName(string? name, int index)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Layout " + (index + 1);
            string trimmed = name.Trim();
            char[] chars = new char[Math.Min(24, trimmed.Length)];
            int count = 0;
            foreach (char ch in trimmed)
            {
                if (count == chars.Length) break;
                if (!char.IsControl(ch) && ch != '\n' && ch != '\r') chars[count++] = ch;
            }
            string cleaned = new string(chars, 0, count).Trim();
            return cleaned.Length == 0 ? "Layout " + (index + 1) : cleaned;
        }

        private void BindSavedLayouts()
        {
            for (int i = 0; i < _layoutPresets.Length; i++)
            {
                _layoutPresets[i] = new LayoutPreset(Config, i + 1);
                _layoutNameDrafts[i] = CleanLayoutName(_layoutPresets[i].Name.Value, i);
            }
        }

        private void RenameLayout(int index)
        {
            LayoutPreset preset = _layoutPresets[index];
            string name = CleanLayoutName(_layoutNameDrafts[index], index);
            _layoutNameDrafts[index] = name;
            preset.Name.Value = name;
            _presetMessage = "Renamed slot " + (index + 1) + " to '" + name + "'.";
            SaveSettings();
        }

        private void SaveLayout(int index)
        {
            // Do not produce a misleading partial preset when the window is closed,
            // fullscreen, still initializing, or being actively resized.
            GameWindow? map = GetEditableTacticalDisplay();
            if (map == null)
            {
                _presetMessage = "Open the windowed Tactical Display before saving.";
                return;
            }
            if (!TryGetMapGeometry(map, out Rect rect))
            {
                _presetMessage = "Unable to read Tactical Display size/position.";
                return;
            }
            if (_autoCenterEnabled && !_autoCalculated)
            {
                _presetMessage = "Wait for Auto-center to settle before saving.";
                return;
            }
            LayoutPreset preset = _layoutPresets[index];
            preset.Left.Value = rect.xMin;
            preset.Top.Value = rect.yMin;
            preset.Width.Value = rect.width;
            preset.Height.Value = rect.height;
            preset.CameraX.Value = _enabled ? EffectiveHorizontal : 0f;
            preset.CameraY.Value = _enabled ? EffectiveVertical : 0f;
            preset.CameraEnabled.Value = _enabled;
            string name = CleanLayoutName(_layoutNameDrafts[index], index);
            _layoutNameDrafts[index] = name;
            preset.Name.Value = name;
            preset.IsSaved.Value = true; // Commit the slot only after all values are set.
            _presetMessage = "Saved map + camera as '" + name + "'.";
            SaveSettings();
        }

        private void LoadLayout(int index)
        {
            LayoutPreset preset = _layoutPresets[index];
            if (!preset.IsSaved.Value) return;
            GameWindow? map = GetEditableTacticalDisplay();
            if (map == null)
            {
                _presetMessage = "Open the windowed Tactical Display before loading.";
                return;
            }
            try
            {
                float screenW = Screen.width / GetUiScale();
                float screenH = Screen.height / GetUiScale();
                float normW = preset.Width.Value;
                float normH = preset.Height.Value;
                if (!ValidNoesisDimension(screenW) || !ValidNoesisDimension(screenH) ||
                    float.IsNaN(normW) || float.IsInfinity(normW) ||
                    float.IsNaN(normH) || float.IsInfinity(normH) ||
                    normW < 0.05f || normW > 1f || normH < 0.05f || normH > 1f)
                {
                    _presetMessage = "Saved layout dimensions invalid. Slot unchanged.";
                    return;
                }
                if (!ResolveMapSize(map, normW * screenW, normH * screenH,
                    screenW, screenH, out float width, out float height))
                {
                    _presetMessage = "Saved map size exceeds current screen/game limits.";
                    return;
                }
                float left = Mathf.Clamp01(preset.Left.Value);
                float top = Mathf.Clamp01(preset.Top.Value);
                if (float.IsNaN(left) || float.IsNaN(top) ||
                    float.IsInfinity(left) || float.IsInfinity(top))
                {
                    _presetMessage = "Saved layout position invalid. Slot unchanged.";
                    return;
                }
                float x = Mathf.Clamp(left * screenW, 0f, Mathf.Max(0f, screenW - width));
                float y = Mathf.Clamp(top * screenH, 0f, Mathf.Max(0f, screenH - height));
                float camX = Mathf.Clamp(preset.CameraX.Value, -MaximumOffset, MaximumOffset);
                float camY = Mathf.Clamp(preset.CameraY.Value, -MaximumOffset, MaximumOffset);
                if (float.IsNaN(camX) || float.IsNaN(camY))
                {
                    _presetMessage = "Saved camera values invalid. Slot unchanged.";
                    return;
                }

                SnapshotOriginalGeometry(map);
                if (!_splitLayoutActive)
                {
                    _cameraBeforeSplitX = _horizontal;
                    _cameraBeforeSplitY = _vertical;
                    _cameraBeforeSplitEnabled = _enabled;
                }
                // Reapply the window geometry in the same proven order as the
                // eight fixed presets. Keep the game UI and its contents intact.
                map.AnchorBottom = false;
                map.AnchorRight = false;
                map.Width = width;
                map.Height = height;
                map.X = x;
                map.Y = y;
                _splitLayoutActive = true;

                _enabled = preset.CameraEnabled.Value;
                _pendingLayoutAutoTrim = false;
                ChangeOffset(camX, camY); // Backup manual view and Auto-off fallback.
                if (_autoCenterEnabled && _enabled)
                {
                    ChangeAutoTrim(0f, 0f);
                    ResetAutoCenterObservation();
                    _pendingLayoutCameraX = camX;
                    _pendingLayoutCameraY = camY;
                    _pendingLayoutAutoTrim = true;
                    _autoStatus = "Auto: restoring saved framing after map settles...";
                }
                else
                {
                    ResetAutoCenterObservation();
                    if (!_enabled) RestoreTrackedProjection();
                }
                _pendingSave = true;
                SaveSettings();
                _tacticalStatus = "Loaded '" + preset.Name.Value + "' (map and camera).";
                _presetMessage = "Loaded '" + preset.Name.Value + "'; Auto-center mode unchanged.";
            }
            catch (Exception ex)
            {
                _presetMessage = "Layout load failed; see BepInEx log.";
                Logger.LogWarning("Legacy saved layout load: " + ex);
            }
        }

        // In Auto-center mode the saved projection is an absolute onscreen shift.
        // Convert it to a relative trim only AFTER the restored map's new geometry
        // has settled. This keeps Auto-center checked and reproduces the saved view.
        private void ApplyPendingLayoutAutoTrim()
        {
            if (!_pendingLayoutAutoTrim || !_autoCenterEnabled || !_autoCalculated) return;
            _pendingLayoutAutoTrim = false;
            _autoTrimX = Mathf.Clamp(_pendingLayoutCameraX - _autoTargetX, -MaximumOffset, MaximumOffset);
            _autoTrimY = Mathf.Clamp(_pendingLayoutCameraY - _autoTargetY, -MaximumOffset, MaximumOffset);
            _pendingSave = true;
            SaveSettings();
            _autoStatus = "Auto: restored saved camera framing.";
        }

        private void DrawSavedLayoutCard(int index, float x, float y)
        {
            LayoutPreset preset = _layoutPresets[index];
            GUI.Box(new Rect(x, y, 195f, 66f), "");
            GUI.Label(new Rect(x + 7f, y + 4f, 18f, 19f), (index + 1).ToString() + ".");
            _layoutNameDrafts[index] = GUI.TextField(
                new Rect(x + 26f, y + 4f, 161f, 21f), _layoutNameDrafts[index], 24);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && preset.IsSaved.Value;
            if (GUI.Button(new Rect(x + 7f, y + 27f, 53f, 20f), "Load")) LoadLayout(index);
            GUI.enabled = oldEnabled;
            if (GUI.Button(new Rect(x + 66f, y + 27f, 53f, 20f), "Save")) SaveLayout(index);
            if (GUI.Button(new Rect(x + 125f, y + 27f, 63f, 20f), "Rename")) RenameLayout(index);
            string summary = preset.IsSaved.Value
                ? "Map " + Mathf.RoundToInt(preset.Width.Value * 100f) + "x" +
                  Mathf.RoundToInt(preset.Height.Value * 100f) + "%  |  " +
                  (preset.CameraEnabled.Value
                    ? "Cam " + Mathf.RoundToInt(preset.CameraX.Value * 100f) + "/" +
                      Mathf.RoundToInt(preset.CameraY.Value * 100f)
                    : "Camera off")
                : "Empty - Save to fill slot";
            GUI.Label(new Rect(x + 8f, y + 48f, 180f, 17f), summary);
        }
    }
}

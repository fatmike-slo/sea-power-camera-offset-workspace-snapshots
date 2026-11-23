using BepInEx.Configuration;
using UnityEngine;

namespace CustomCameraOffset
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> _uiAutoScaleSetting = null!;
        private ConfigEntry<int> _uiScalePercentSetting = null!;
        private float _uiScale = 1f;
        // The slider previews a requested size without moving under the cursor.
        // We apply its new scale on mouse release, then persist it once.
        private int _uiScalePendingPercent = -1;
        private int _uiPreviousScreenWidth = -1;
        private int _uiPreviousScreenHeight = -1;

        private void BindUiScaleConfig()
        {
            _uiAutoScaleSetting = Config.Bind("UI", "AutoScale", true,
                "Automatically use a larger mod interface on high-resolution displays. Changeable in-game.");
            _uiScalePercentSetting = Config.Bind("UI", "ScalePercent", 100,
                new ConfigDescription("Manual interface scaling (100-200%, in steps of 1%). " +
                    "Used when AutoScale is false. Both mod windows and their buttons scale.",
                    new AcceptableValueRange<int>(100, 200)));
        }

        private float RecommendedUiScale()
        {
            // Prefer display height: an ultrawide 3440x1440 monitor should not
            // automatically receive the same scale as a 3840x2160 monitor.
            if (Screen.height >= 2000) return 1.75f;
            if (Screen.height >= 1300) return 1.25f;
            return 1f;
        }

        private float CalculateUiScale()
        {
            float requested = _uiAutoScaleSetting.Value ? RecommendedUiScale() :
                Mathf.Clamp(_uiScalePercentSetting.Value, 100, 200) / 100f;

            // Preserve usable controls on smaller screens. The expanded
            // workspace panel has its own scroll view. Reserve at least 600
            // logical vertical pixels so the size controls and footer remain
            // usable even when the workspace panel needs scrolling.
            float widthFit = Mathf.Max(1f, (Screen.width - 24f) / WindowWidth);
            float heightFit = Mathf.Max(1f, (Screen.height - 24f) / 600f);
            return Mathf.Clamp(Mathf.Min(requested, widthFit, heightFit), 1f, 2f);
        }

        private void RefreshUiScale()
        {
            bool resolutionChanged = Screen.width != _uiPreviousScreenWidth ||
                Screen.height != _uiPreviousScreenHeight;
            float newScale = CalculateUiScale();
            if (!resolutionChanged && Mathf.Approximately(newScale, _uiScale)) return;

            _uiPreviousScreenWidth = Screen.width;
            _uiPreviousScreenHeight = Screen.height;
            _uiScale = newScale;
            // Launcher position is stored normalized, not as absolute pixels.
            // Reloading those coordinates prevents drifting between resolutions.
            _launcherPositionInitialized = false;
            _recenterOnNextDraw = true;
        }

        private void SetUiScaleAuto(bool auto)
        {
            _uiScalePendingPercent = -1;
            if (_uiAutoScaleSetting.Value == auto) return;
            _uiAutoScaleSetting.Value = auto;
            SaveSettings();
        }

        private void SetManualUiScale(int percent)
        {
            percent = Mathf.Clamp(percent, 100, 200);
            _uiScalePendingPercent = -1;
            if (!_uiAutoScaleSetting.Value && _uiScalePercentSetting.Value == percent) return;
            _uiAutoScaleSetting.Value = false;
            _uiScalePercentSetting.Value = percent;
            SaveSettings();
        }

        private void CommitPendingUiScale()
        {
            if (_uiScalePendingPercent < 0) return;
            int newPercent = _uiScalePendingPercent;
            _uiScalePendingPercent = -1;
            SetManualUiScale(newPercent);
        }

        private void DrawUiScaleControls(float y)
        {
            GUI.Label(new Rect(22f, y + 4f, 54f, 21f), "UI size");

            int displayedPercent = _uiScalePendingPercent >= 0 ? _uiScalePendingPercent :
                (_uiAutoScaleSetting.Value ? Mathf.RoundToInt(_uiScale * 100f) :
                    _uiScalePercentSetting.Value);
            // Continuous slider, 1% display precision. Defer applying the GUI
            // transform until release, so it cannot jump away from the cursor.
            float moved = GUI.HorizontalSlider(new Rect(78f, y + 6f, 151f, 17f),
                displayedPercent, 100f, 200f);
            int requestedPercent = Mathf.Clamp(Mathf.RoundToInt(moved), 100, 200);
            if (requestedPercent != displayedPercent)
                _uiScalePendingPercent = requestedPercent;

            int labelPercent = _uiScalePendingPercent >= 0 ? _uiScalePendingPercent : displayedPercent;
            GUI.Label(new Rect(240f, y + 4f, 52f, 21f), labelPercent + "%");

            bool autoNow = GUI.Toggle(new Rect(294f, y + 2f, 63f, 23f),
                _uiAutoScaleSetting.Value && _uiScalePendingPercent < 0, "Auto");
            if (autoNow != (_uiAutoScaleSetting.Value && _uiScalePendingPercent < 0))
            {
                if (autoNow) SetUiScaleAuto(true);
                else if (_uiScalePendingPercent < 0) SetManualUiScale(displayedPercent);
            }

            if (GUI.Button(new Rect(360f, y, 59f, 26f),
                new GUIContent("100%", "Reset interface size to 100%")))
                SetManualUiScale(100);

            Event current = Event.current;
            if (_uiScalePendingPercent >= 0 && current != null &&
                current.rawType == EventType.MouseUp)
                CommitPendingUiScale();
        }
    }
}

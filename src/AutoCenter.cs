using System;
using BepInEx.Configuration;
using UnityEngine;
using GameWindow = SeapowerUI.Window;

namespace CustomCameraOffset
{
    public sealed partial class Plugin
    {
        private ConfigEntry<bool> _autoCenterSetting = null!;
        private ConfigEntry<float> _autoTrimXSetting = null!;
        private ConfigEntry<float> _autoTrimYSetting = null!;
        private bool _autoCenterEnabled;
        private float _autoTrimX;
        private float _autoTrimY;
        private float _autoTargetX;
        private float _autoTargetY;
        private bool _autoCalculated;
        private string _autoStatus = "Auto-center off (manual offsets active).";
        private float _nextAutoProbe;
        private float _lastGeometryChange;
        private Rect _lastMapGeometry;
        private GameWindow? _lastAutoMap;
        private bool _haveGeometry;
        private int _lastAutoRegion = -1;

        // In fullscreen-map/PiP mode, never fall back to saved manual offsets.
        // That fallback caused the miniature World View to remain displaced.
        private float EffectiveHorizontal => _fullscreenMapSuspended ? 0f : _autoCenterEnabled && _autoCalculated
            ? Mathf.Clamp(_autoTargetX + _autoTrimX, -MaximumOffset, MaximumOffset)
            : _horizontal;
        private float EffectiveVertical => _fullscreenMapSuspended ? 0f : _autoCenterEnabled && _autoCalculated
            ? Mathf.Clamp(_autoTargetY + _autoTrimY, -MaximumOffset, MaximumOffset)
            : _vertical;

        private void BindAutoCenterConfig()
        {
            _autoCenterSetting = Config.Bind("Auto Center", "Enabled", false,
                "Automatically center the 3D subject in the largest unobstructed rectangle when the Tactical Display is open. Defaults off.");
            _autoTrimXSetting = Config.Bind("Auto Center", "TrimHorizontal", 0f,
                "Fine-tune horizontal position in Auto-center mode, independent of existing manual camera presets.");
            _autoTrimYSetting = Config.Bind("Auto Center", "TrimVertical", 0f,
                "Fine-tune vertical position in Auto-center mode, independent of existing manual camera presets.");
        }

        private void LoadAutoCenterConfig()
        {
            _autoCenterEnabled = _autoCenterSetting.Value;
            _autoTrimX = Mathf.Clamp(_autoTrimXSetting.Value, -MaximumOffset, MaximumOffset);
            _autoTrimY = Mathf.Clamp(_autoTrimYSetting.Value, -MaximumOffset, MaximumOffset);
            _autoStatus = _autoCenterEnabled ? "Auto: waiting for an open Tactical Display." :
                "Auto-center off (manual offsets active).";
        }

        private void SaveAutoCenterConfig()
        {
            _autoCenterSetting.Value = _autoCenterEnabled;
            _autoTrimXSetting.Value = _autoTrimX;
            _autoTrimYSetting.Value = _autoTrimY;
        }

        private void SetAutoCenterEnabled(bool value)
        {
            if (_autoCenterEnabled == value) return;
            _autoCenterEnabled = value;
            _pendingLayoutAutoTrim = false;
            ResetAutoCenterObservation();
            if (value)
            {
                // The split-layout buttons are deliberate fixed views. Auto-center
                // becomes a new mode only when the player explicitly selects it.
                _enabled = true;
                _autoStatus = "Auto: observing Tactical Display geometry...";
            }
            else
            {
                _autoStatus = "Auto-center off (manual offsets active).";
            }
            _pendingSave = true;
            SaveSettings();
        }

        private void ChangeAutoTrim(float x, float y)
        {
            x = Mathf.Clamp(x, -MaximumOffset, MaximumOffset);
            y = Mathf.Clamp(y, -MaximumOffset, MaximumOffset);
            if (Mathf.Approximately(x, _autoTrimX) && Mathf.Approximately(y, _autoTrimY)) return;
            _autoTrimX = x;
            _autoTrimY = y;
            _pendingLayoutAutoTrim = false; // Direct user fine-tune cancels deferred layout restore.
            _pendingSave = true;
        }

        private void ResetAutoCenterObservation()
        {
            _autoCalculated = false;
            _haveGeometry = false;
            _lastAutoMap = null;
            _lastAutoRegion = -1;
            _nextAutoProbe = 0f;
            _lastGeometryChange = 0f;
        }

        private static bool ApproximatelySameRect(Rect a, Rect b)
        {
            return Mathf.Abs(a.x - b.x) < 0.003f &&
                   Mathf.Abs(a.y - b.y) < 0.003f &&
                   Mathf.Abs(a.width - b.width) < 0.003f &&
                   Mathf.Abs(a.height - b.height) < 0.003f;
        }

        private bool TryGetMapGeometry(GameWindow map, out Rect bounds)
        {
            bounds = new Rect();
            if (!map.IsLoaded || map.Maximized || IsFullscreenTacticalDisplay(map)) return false;
            if (map.Visibility != Noesis.Visibility.Visible) return false;
            float width = map.ActualWidth;
            float height = map.ActualHeight;
            if (!ValidNoesisDimension(width)) width = map.Width;
            if (!ValidNoesisDimension(height)) height = map.Height;
            if (!ValidNoesisDimension(width) || !ValidNoesisDimension(height)) return false;
            float uiScale = GetUiScale();
            float viewWidth = Screen.width / uiScale;
            float viewHeight = Screen.height / uiScale;
            if (!ValidNoesisDimension(viewWidth) || !ValidNoesisDimension(viewHeight)) return false;
            // GameWindow's X/Y are host coordinates. Anchored windows store a
            // distance from the corresponding edge rather than the left/top.
            float x = map.AnchorRight ? viewWidth - map.X - width : map.X;
            float y = map.AnchorBottom ? viewHeight - map.Y - height : map.Y;
            // Convert the map's actual bounds to normalized top-left-origin units.
            float left = Mathf.Clamp01(x / viewWidth);
            float top = Mathf.Clamp01(y / viewHeight);
            float right = Mathf.Clamp01((x + width) / viewWidth);
            float bottom = Mathf.Clamp01((y + height) / viewHeight);
            if (right <= left || bottom <= top) return false;
            bounds = Rect.MinMaxRect(left, top, right, bottom);
            return true;
        }

        private static bool TryQuarterCornerFreeCenter(Rect map, out float x, out float y)
        {
            x = 0f;
            y = 0f;
            // Respect the existing largest-rectangle algorithm for every other
            // user-defined map size/location. Quarter layouts have a special
            // L-shaped visible region whose centroid changes in BOTH axes.
            bool quarter = Mathf.Abs(map.width - 0.5f) <= 0.085f &&
                           Mathf.Abs(map.height - 0.5f) <= 0.085f;
            bool atHorizontalEdge = map.xMin <= 0.025f || map.xMax >= 0.975f;
            bool atVerticalEdge = map.yMin <= 0.025f || map.yMax >= 0.975f;
            if (!quarter || !atHorizontalEdge || !atVerticalEdge) return false;
            float occupied = map.width * map.height;
            float free = 1f - occupied;
            if (free <= 0.05f) return false;
            float freeCenterX = (0.5f - occupied * map.center.x) / free;
            float freeCenterY = (0.5f - occupied * map.center.y) / free;
            x = Mathf.Clamp(freeCenterX - 0.5f, -MaximumOffset, MaximumOffset);
            y = Mathf.Clamp(0.5f - freeCenterY, -MaximumOffset, MaximumOffset);
            return true;
        }

        private void PollAutoCenter()
        {
            if (_fullscreenMapSuspended)
            {
                _autoCalculated = false;
                _autoStatus = "Fullscreen Tactical Display: camera offset temporarily suspended.";
                return;
            }
            if (!_autoCenterEnabled)
            {
                _autoCalculated = false;
                return;
            }
            if (Time.unscaledTime < _nextAutoProbe) return;
            _nextAutoProbe = Time.unscaledTime + 0.15f;

            GameWindow? map = FindTacticalDisplay();
            if (map == null || !TryGetMapGeometry(map, out Rect geometry))
            {
                _autoCalculated = false;
                _haveGeometry = false;
                _lastAutoMap = null;
                _autoStatus = "Auto: waiting for a windowed Tactical Display.";
                return;
            }
            if (!_haveGeometry || !ReferenceEquals(_lastAutoMap, map) ||
                !ApproximatelySameRect(geometry, _lastMapGeometry))
            {
                _haveGeometry = true;
                _lastAutoMap = map;
                _lastMapGeometry = geometry;
                _lastGeometryChange = Time.unscaledTime;
                _autoStatus = "Auto: map moving/resizing; settling...";
                return;
            }
            // Debounce window drag/resize. Camera will not chase every UI frame.
            if (Time.unscaledTime - _lastGeometryChange < 0.30f) return;

            if (TryQuarterCornerFreeCenter(geometry, out float quarterX, out float quarterY))
            {
                bool quarterChanged = !_autoCalculated ||
                    Mathf.Abs(_autoTargetX - quarterX) > 0.001f ||
                    Mathf.Abs(_autoTargetY - quarterY) > 0.001f;
                _lastAutoRegion = -1;
                _autoTargetX = quarterX;
                _autoTargetY = quarterY;
                _autoCalculated = true;
                ApplyPendingLayoutAutoTrim();
                if (quarterChanged)
                    _autoStatus = "Auto: centered in 75% L-shaped viewing area";
                return;
            }

            float left = geometry.xMin, top = geometry.yMin;
            float right = geometry.xMax, bottom = geometry.yMax;
            // Four maximal axis-aligned rectangles not occluded by the map.
            // X/Y use screen top-left origin; the resulting vertical camera
            // offset is inverted to Unity's bottom-left viewport origin.
            Rect[] candidates =
            {
                Rect.MinMaxRect(0f, 0f, left, 1f),
                Rect.MinMaxRect(right, 0f, 1f, 1f),
                Rect.MinMaxRect(0f, 0f, 1f, top),
                Rect.MinMaxRect(0f, bottom, 1f, 1f)
            };
            int selected = -1;
            float bestScore = 0.05f; // Smaller than 5% is not a usable 3D region.
            for (int i = 0; i < candidates.Length; ++i)
            {
                Rect candidate = candidates[i];
                float area = candidate.width * candidate.height;
                // Light hysteresis prevents two almost equal regions flipping
                // back and forth from tiny map edge changes.
                float score = area + (i == _lastAutoRegion ? 0.015f : 0f);
                if (area < 0.05f || score <= bestScore) continue;
                selected = i;
                bestScore = score;
            }
            if (selected < 0)
            {
                _autoCalculated = false;
                _autoStatus = "Auto: map covers the view; centering suspended.";
                return;
            }
            Rect free = candidates[selected];
            float targetX = free.center.x - 0.5f;
            float targetY = 0.5f - free.center.y;
            bool changed = !_autoCalculated ||
                Mathf.Abs(_autoTargetX - targetX) > 0.001f ||
                Mathf.Abs(_autoTargetY - targetY) > 0.001f;
            _lastAutoRegion = selected;
            _autoTargetX = targetX;
            _autoTargetY = targetY;
            _autoCalculated = true;
            ApplyPendingLayoutAutoTrim();
            if (changed)
            {
                bool clipped = Mathf.Abs(targetX + _autoTrimX) > MaximumOffset ||
                               Mathf.Abs(targetY + _autoTrimY) > MaximumOffset;
                _autoStatus = "Auto: 3D region " +
                    Mathf.RoundToInt(free.width * 100f) + "% x " +
                    Mathf.RoundToInt(free.height * 100f) + "%" +
                    (clipped ? " (camera shift limited)" : " (centered)");
            }
        }
    }
}

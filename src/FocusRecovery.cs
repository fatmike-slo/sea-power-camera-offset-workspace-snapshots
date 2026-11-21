using System;
using System.Reflection;
using Noesis;
using UnityEngine;
using GameWindow = SeapowerUI.Window;

namespace CustomCameraOffset
{
    public sealed partial class Plugin
    {
        // Closing Ctrl+Shift+U can leave focus in Sea Power's Noesis UI even after its
        // hit-testing has been restored. Delay transfer until the pointer is
        // physically over free 3D space, never while selecting on the tactical
        // map, Event Log, Formation Manager or another floating game window.
        private bool _worldFocusPending;
        private float _worldFocusDeadline;
        private bool _focusRecoveryWarningLogged;

        private void ArmWorldFocusHandoff()
        {
            if (!_missionActive || !Application.isFocused) return;
            _worldFocusPending = true;
            _worldFocusDeadline = Time.unscaledTime + 12f;
        }

        private void PollWorldFocusHandoff()
        {
            if (!_worldFocusPending) return;
            if (!_missionActive || !Application.isFocused || Time.unscaledTime > _worldFocusDeadline)
            {
                _worldFocusPending = false;
                return;
            }
            // Do not take a pressed mouse button away from Noesis: that can
            // swallow a map/window selection. Clear after physical release.
            if (_visible || IsLeftMouseDown() || _mouseDragCaptured ||
                _launcherMouseCaptured || PointerWithinLauncher()) return;
            if (PointerOverGameUiWindow()) return;
            _worldFocusPending = false;
            ReleaseNoesisFocusForWorld();
        }

        private bool PointerOverGameUiWindow()
        {
            return IsPointerOverVisibleNoesisWindow();
        }

        // Visual-tree hit test only asks if the pointer is inside an existing
        // floating game window. It does not change selection or UI state.
        private bool IsPointerOverVisibleNoesisWindow()
        {
            try
            {
                SeapowerUI.MainGameView? view = SeapowerUI.MainGameView.Instance;
                if (view is not UIElement root) return true; // Conservative: no UI -> no handoff.
                Vector3 position = Input.mousePosition;
                float scale = GetUiScale();
                var point = new Point(position.x / scale, (Screen.height - position.y) / scale);
                var hit = VisualTreeHelper.HitTest(root, point);
                DependencyObject? node = hit.VisualHit;
                int depth = 0;
                while (node != null && depth++ < 80)
                {
                    if (node is GameWindow) return true;
                    node = VisualTreeHelper.GetParent(node);
                }
                return false;
            }
            catch (Exception ex)
            {
                ReportFocusRecoveryWarning(ex);
                return true; // Fail closed; never steal focus on unknown UI.
            }
        }

        private void ReleaseNoesisFocusForWorld()
        {
            try
            {
                SeapowerUI.MainGameView? view = SeapowerUI.MainGameView.Instance;
                if (view is not UIElement root) return;

                // Noesis versions differ: Keyboard.ClearFocus() and
                // Keyboard.Focus(null) are supported in different bindings.
                PropertyInfo? keyboardProperty = typeof(UIElement).GetProperty("Keyboard",
                    BindingFlags.Public | BindingFlags.Instance);
                object? keyboard = keyboardProperty?.GetValue(root);
                if (keyboard != null)
                {
                    Type type = keyboard.GetType();
                    MethodInfo? clearFocus = type.GetMethod("ClearFocus", Type.EmptyTypes);
                    if (clearFocus != null) clearFocus.Invoke(keyboard, null);
                    else
                    {
                        MethodInfo? focus = type.GetMethod("Focus", new[] { typeof(UIElement) });
                        focus?.Invoke(keyboard, new object?[] { null });
                    }
                }
                // Only release a stale mouse capture when outside all windows
                // AND no physical mouse button is pressed. This prevents the
                // lost-click behavior reported by Noesis users.
                PropertyInfo? mouseProperty = typeof(UIElement).GetProperty("Mouse",
                    BindingFlags.Public | BindingFlags.Instance);
                object? mouse = mouseProperty?.GetValue(root);
                if (mouse != null)
                {
                    Type type = mouse.GetType();
                    PropertyInfo? captured = type.GetProperty("Captured",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (captured?.GetValue(mouse) != null)
                    {
                        MethodInfo? capture = type.GetMethod("Capture", new[] { typeof(UIElement) });
                        capture?.Invoke(mouse, new object?[] { null });
                    }
                }
                Logger.LogDebug("Ctrl+Shift+U focus handoff: released Noesis focus over 3D scene.");
            }
            catch (Exception ex) { ReportFocusRecoveryWarning(ex); }
        }

        private void ReportFocusRecoveryWarning(Exception ex)
        {
            if (_focusRecoveryWarningLogged) return;
            _focusRecoveryWarningLogged = true;
            Logger.LogWarning("Ctrl+Shift+U 3D focus handoff unavailable: " + ex.Message);
        }
    }
}

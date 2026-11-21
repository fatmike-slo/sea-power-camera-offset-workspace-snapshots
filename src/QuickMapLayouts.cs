using System;
using UnityEngine;
using GUI = UnityEngine.GUI;
using Rect = UnityEngine.Rect;

namespace CustomCameraOffset
{
    public sealed partial class Plugin
    {
        // Snapshot persistence uses these stable role IDs when saving windows.
        // Quick map layouts do not access or change these additional windows.
        private static readonly string[] ManagedPanelKeys = {
            "panel:formation", "panel:eventlog", "panel:status",
            "panel:reference", "panel:camera"
        };

        private void ShowAllManagedPanels()
        {
            // Explicit recovery only: Quick Map Layout tiles never change other windows.
            bool wasExpanded = _workspacePage;
            ClearWorkspaceRuntimeState();
            _workspacePage = wasExpanded;
            int shown = 0;
            foreach (var item in UniqueWorkspaceWindows(CurrentWorkspaceWindows(true)))
            {
                if (!item.Key.StartsWith("panel:", StringComparison.Ordinal)) continue;
                if (item.Value.Visibility != Noesis.Visibility.Visible)
                {
                    item.Value.Visibility = Noesis.Visibility.Visible;
                    shown++;
                }
            }
            _workspaceMessage = "Revealed " + shown + " initialized panels.";
        }

        private void DrawQuickMapTile(SplitLayout layout, float x, float y, string label)
        {
            const float tileWidth = 98f;
            const float tileHeight = 49f;
            if (GUI.Button(new Rect(x, y, tileWidth, tileHeight), ""))
                ApplySplitLayout(layout);
            // v0.9.0-style visual representation: dark 3D area, light tactical map.
            Rect outer = new Rect(x + 16f, y + 3f, 66f, 24f);
            GUI.Box(outer, "");
            Rect inner = new Rect(outer.x + 3f, outer.y + 3f,
                outer.width - 6f, outer.height - 6f);
            Color previous = GUI.color;
            GUI.color = new Color(0.20f, 0.36f, 0.53f, 1f);
            GUI.DrawTexture(inner, Texture2D.whiteTexture);
            Rect mapArea = inner;
            switch (layout)
            {
                case SplitLayout.MapLeft: mapArea.width *= 0.5f; break;
                case SplitLayout.MapRight: mapArea.x += mapArea.width * 0.5f; mapArea.width *= 0.5f; break;
                case SplitLayout.MapTop: mapArea.height *= 0.5f; break;
                case SplitLayout.MapBottom: mapArea.y += mapArea.height * 0.5f; mapArea.height *= 0.5f; break;
                case SplitLayout.MapTopLeft:
                    mapArea.width *= 0.5f; mapArea.height *= 0.5f; break;
                case SplitLayout.MapBottomLeft:
                    mapArea.y += mapArea.height * 0.5f;
                    mapArea.width *= 0.5f; mapArea.height *= 0.5f; break;
                case SplitLayout.MapTopRight:
                    mapArea.x += mapArea.width * 0.5f;
                    mapArea.width *= 0.5f; mapArea.height *= 0.5f; break;
                case SplitLayout.MapBottomRight:
                    mapArea.x += mapArea.width * 0.5f;
                    mapArea.y += mapArea.height * 0.5f;
                    mapArea.width *= 0.5f; mapArea.height *= 0.5f; break;
            }
            GUI.color = new Color(0.61f, 0.79f, 0.88f, 1f);
            GUI.DrawTexture(mapArea, Texture2D.whiteTexture);
            GUI.color = previous;
            GUI.Label(new Rect(x + 2f, y + 29f, 94f, 18f), label);
        }
    }
}

#if ANCHORCHAIN_WORKSHOP
using AnchorChain;
using BepInEx;
using BepInEx.Bootstrap;
using System;
using System.IO;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CustomCameraOffset
{
    /// <summary>
    /// Anchor Chain entry point. Compiled only in the Workshop build.
    /// The normal BepInEx version remains independent of this code.
    /// </summary>
    [ACPlugin("local.seapower.customcameraoffset", "Camera Offset & Workspace Snapshots", "1.0.2")]
    public sealed class WorkshopEntryPoint : IAnchorChainMod
    {
        // Shared across Assembly.LoadFile copies: Assembly static fields are NOT shared there.
        private const string LoadedMarker = "local.seapower.customcameraoffset.workshop.loaded";
        private static GameObject? _host;

        public void TriggerEntryPoint()
        {
            // Different Workshop directories may load this DLL into separate assembly contexts.
            if (AppDomain.CurrentDomain.GetData(LoadedMarker) != null || _host != null)
            {
                Debug.LogWarning("[Camera Offset & Workspace Snapshots] Another Workshop copy already started; skipping.");
                return;
            }

            try
            {
                // BepInEx records discovered plugins by GUID. This is an additional guard;
                // the guarded local installer also moves the manual DLL outside the plugins tree.
                if (Chainloader.PluginInfos.ContainsKey("local.seapower.customcameraoffset"))
                {
                    Debug.LogWarning("[Camera Offset & Workspace Snapshots] Manual BepInEx plugin already loaded; Workshop copy inactive.");
                    return;
                }

                // Search all BepInEx plugin subfolders, not just the conventional location.
                // Any filesystem error fails closed, rather than risking two camera controllers.
                if (Directory.Exists(Paths.PluginPath))
                {
                    foreach (string dll in Directory.GetFiles(Paths.PluginPath,
                                 "CustomCameraOffset.dll", SearchOption.AllDirectories))
                    {
                        Debug.LogWarning("[Camera Offset & Workspace Snapshots] Manual DLL detected at " + dll +
                            "; Workshop copy inactive. Move the manual DLL outside BepInEx/plugins.");
                        return;
                    }
                }

                AppDomain.CurrentDomain.SetData(LoadedMarker, true);
                _host = new GameObject("Camera Offset & Workspace Snapshots (Anchor Chain v1.0.2)");
                Object.DontDestroyOnLoad(_host);
                _host.AddComponent<Plugin>();
                Debug.Log("[Camera Offset & Workspace Snapshots] Workshop adapter started (v1.0.2).");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Camera Offset & Workspace Snapshots] Workshop adapter startup FAILED: " + ex);
                if (_host != null)
                {
                    Object.Destroy(_host);
                    _host = null;
                }
                AppDomain.CurrentDomain.SetData(LoadedMarker, null);
                // Do not crash the game because of an integration failure.
            }
        }
    }
}
#endif

using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace GFXConf;

/// <summary>
/// Config schema — mirrors spec §4.2 word-for-word (keys, sections, defaults).
/// Single source of truth for all bindings; the cfg file is the source of
/// truth at runtime.
/// </summary>
internal static class GfxConfig
{
    /// <summary>The gfxconf.cfg file (NOT BepInEx's GUID-based default name).</summary>
    internal static ConfigFile File { get; private set; }

    // BasePlugin.Log is an instance member, so Plugin.Load() registers its
    // ManualLogSource here before Bind() — warnings from this static class
    // still reach LogOutput.log. Null-guarded in case Bind runs standalone.
    internal static ManualLogSource LogSource { get; set; }

    // [PPv2]
    internal static ConfigEntry<bool> DisableAmbientOcclusion;
    internal static ConfigEntry<bool> DisableChromaticAberration;
    internal static ConfigEntry<bool> DisableDepthOfField;
    internal static ConfigEntry<bool> DisableScreenSpaceReflections;
    internal static ConfigEntry<bool> DisableMotionBlur;
    internal static ConfigEntry<bool> DisableBloom;

    // [SCPE]
    internal static ConfigEntry<bool> DisableFog;
    internal static ConfigEntry<bool> DisableCloudShadows;
    internal static ConfigEntry<bool> DisableAmbientOcclusion2D;
    internal static ConfigEntry<bool> DisableBlur;
    internal static ConfigEntry<bool> DisableSharpen;

    // [Volumetrics]
    internal static ConfigEntry<bool> DisableVolumetricFog;
    internal static ConfigEntry<bool> DisablePlanarReflections;
    internal static ConfigEntry<bool> DisableAura2;

    // [Antialiasing]
    internal static ConfigEntry<string> OverrideMode;

    // [General]
    internal static ConfigEntry<bool> ReapplyOnSceneLoad;
    internal static ConfigEntry<int> DelaySeconds;
    internal static ConfigEntry<bool> EnableF10Overlay;

    // [Scenes] — intentionally config-file-only; the allowlist is not a text field in the F10 overlay.
    internal static ConfigEntry<string> SceneSuppressionAllowlist;
    internal static ConfigEntry<KeyCode> CaptureSceneHotkey;

    /// <summary>
    /// Toggle entries keyed by the concrete settings type name, for the PPv2
    /// and SCPE sweep groups. The 3 Volumetrics toggles are component groups
    /// (name lookup in the sweep), not settings types — intentionally absent.
    /// </summary>
    internal static IReadOnlyDictionary<string, ConfigEntry<bool>> SettingToggles { get; private set; }
        = new Dictionary<string, ConfigEntry<bool>>();

    /// <summary>
    /// Binds all entries to &lt;game&gt;/BepInEx/config/gfxconf.cfg and saves
    /// so every key exists on disk after first run. Never throws — a failed
    /// bind logs one warning and leaves the entry on its BepInEx default path.
    /// </summary>
    internal static void Bind()
    {
        try
        {
            File = new ConfigFile(Path.Combine(Paths.ConfigPath, "gfxconf.cfg"), saveOnInit: false);

            DisableAmbientOcclusion = File.Bind("PPv2", nameof(DisableAmbientOcclusion), true, "Disable the PPv2 Ambient Occlusion effect.");
            DisableChromaticAberration = File.Bind("PPv2", nameof(DisableChromaticAberration), true, "Disable the PPv2 Chromatic Aberration effect.");
            DisableDepthOfField = File.Bind("PPv2", nameof(DisableDepthOfField), true, "Disable the PPv2 Depth of Field effect.");
            DisableScreenSpaceReflections = File.Bind("PPv2", nameof(DisableScreenSpaceReflections), true, "Disable the PPv2 Screen Space Reflections effect.");
            DisableMotionBlur = File.Bind("PPv2", nameof(DisableMotionBlur), true, "Disable the PPv2 Motion Blur effect.");
            DisableBloom = File.Bind("PPv2", nameof(DisableBloom), false, "Disable the PPv2 Bloom effect (default false = keep Bloom).");

            DisableFog = File.Bind("SCPE", nameof(DisableFog), true, "Disable the SCPE Fog effect.");
            DisableCloudShadows = File.Bind("SCPE", nameof(DisableCloudShadows), true, "Disable the SCPE Cloud Shadows effect.");
            DisableAmbientOcclusion2D = File.Bind("SCPE", nameof(DisableAmbientOcclusion2D), true, "Disable the SCPE Ambient Occlusion 2D effect.");
            DisableBlur = File.Bind("SCPE", nameof(DisableBlur), true, "Disable the SCPE Blur effect.");
            DisableSharpen = File.Bind("SCPE", nameof(DisableSharpen), true, "Disable the SCPE Sharpen effect.");

            DisableVolumetricFog = File.Bind("Volumetrics", nameof(DisableVolumetricFog), true, "Disable VolumetricFog components (volumetric fog and mist).");
            DisablePlanarReflections = File.Bind("Volumetrics", nameof(DisablePlanarReflections), true, "Disable Ceto PlanarReflection components.");
            DisableAura2 = File.Bind("Volumetrics", nameof(DisableAura2), true, "Disable Aura 2 components (Aura, AuraVolume, AuraCamera).");

            OverrideMode = File.Bind("Antialiasing", nameof(OverrideMode), "KeepOriginal", "PostProcessLayer antialiasing override: KeepOriginal (default) or None | FastFXAA | FXAA | SMAA | TAA - FXAA and FastFXAA both use this build's single FastApproximateAntialiasing mode (FastFXAA enables fastMode).");

            ReapplyOnSceneLoad = File.Bind("General", nameof(ReapplyOnSceneLoad), true, "Re-run the effect sweep after each scene load.");
            DelaySeconds = File.Bind("General", nameof(DelaySeconds), 2, "Seconds to wait after a scene load before the sweep runs and idle-scene capture begins.");
            EnableF10Overlay = File.Bind("General", nameof(EnableF10Overlay), true, "Enable the F10 in-game settings overlay.");

            SceneSuppressionAllowlist = File.Bind("Scenes", nameof(SceneSuppressionAllowlist),
                "SS_Farmland, SS_City_Market, SS_City_Street",
                "Comma-separated runtime scene names where 3D rendering is suppressed. Edit in gfxconf.cfg; use F9 only in confirmed idle scenes.");
            CaptureSceneHotkey = File.Bind("Scenes", nameof(CaptureSceneHotkey), KeyCode.F9,
                "Add the most recently loaded scene to SceneSuppressionAllowlist. Use only in a confirmed idle scene.");

            SettingToggles = new Dictionary<string, ConfigEntry<bool>>
            {
                ["AmbientOcclusion"] = DisableAmbientOcclusion,
                ["ChromaticAberration"] = DisableChromaticAberration,
                ["DepthOfField"] = DisableDepthOfField,
                ["ScreenSpaceReflections"] = DisableScreenSpaceReflections,
                ["MotionBlur"] = DisableMotionBlur,
                ["Bloom"] = DisableBloom,
                ["Fog"] = DisableFog,
                ["CloudShadows"] = DisableCloudShadows,
                ["AmbientOcclusion2D"] = DisableAmbientOcclusion2D,
                ["Blur"] = DisableBlur,
                ["Sharpen"] = DisableSharpen
            };

            File.Save();
        }
        catch (Exception ex)
        {
            LogSource?.LogWarning($"[GFXConf] config bind failed: {ex}");
        }
    }

    /// <summary>Persists the config file; returns false on unavailable config or save failure. Never throws.</summary>
    internal static bool Save()
    {
        try
        {
            if (File == null)
            {
                return false;
            }

            File.Save();
            return true;
        }
        catch (Exception ex)
        {
            LogSource?.LogWarning($"[GFXConf] config save failed: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Parses a comma-separated scene-name list, preserving first occurrence
    /// order for config serialization while matching duplicates case-insensitively.
    /// </summary>
    internal static List<string> ParseSceneSuppressionAllowlist(string value)
    {
        var scenes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in (value ?? string.Empty).Split(','))
        {
            var scene = item.Trim();
            if (scene.Length > 0 && seen.Add(scene))
            {
                scenes.Add(scene);
            }
        }

        return scenes;
    }

    internal static bool IsSceneSuppressionEligible(IEnumerable<string> loadedSceneNames, string allowlistValue)
    {
        if (loadedSceneNames == null)
        {
            return false;
        }

        var allowed = new HashSet<string>(ParseSceneSuppressionAllowlist(allowlistValue), StringComparer.OrdinalIgnoreCase);
        foreach (var sceneName in loadedSceneNames)
        {
            if (!string.IsNullOrWhiteSpace(sceneName) && allowed.Contains(sceneName.Trim()))
            {
                return true;
            }
        }

        return false;
    }
}

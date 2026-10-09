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

    // The old [General] keys are gone in v0.4.0: reapply-on-scene-load and the
    // F10 overlay are always on, and the post-scene delay is a fixed constant.
    /// <summary>Seconds after a scene load before the sweep runs and idle-scene capture begins. Fixed at 3 s.</summary>
    internal const int DelaySeconds = 3;

    // [Quality] — render-quality overrides (Built-in RP QualitySettings); KeepOriginal = no-op.
    internal static ConfigEntry<string> ShadowDistance;
    internal static ConfigEntry<string> ShadowResolution;
    internal static ConfigEntry<string> LodBias;
    internal static ConfigEntry<string> MSAA;

    // [Scenes] — allowlist/hotkey are config-file-only; the master switch below is the one overlay-editable scene key.
    internal static ConfigEntry<string> SceneSuppressionAllowlist;
    internal static ConfigEntry<KeyCode> CaptureSceneHotkey;

    // Master switch for the idle-scene render suppression ("scene pauser").
    internal static ConfigEntry<bool> EnableSceneSuppression;

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

            SceneSuppressionAllowlist = File.Bind("Scenes", nameof(SceneSuppressionAllowlist),
                "SS_Farmland, SS_City_Market, SS_City_Street",
                "Comma-separated runtime scene names where 3D rendering is suppressed. Edit in gfxconf.cfg; use F9 only in confirmed idle scenes.");
            CaptureSceneHotkey = File.Bind("Scenes", nameof(CaptureSceneHotkey), KeyCode.F9,
                "Add the most recently loaded scene to SceneSuppressionAllowlist. Use only in a confirmed idle scene.");
            EnableSceneSuppression = File.Bind("Scenes", nameof(EnableSceneSuppression), true,
                "Master switch for idle-scene 3D render suppression (the frozen-scene 'scene pauser'). Set false to keep live 3D rendering in allowlisted scenes.");

            ShadowDistance = File.Bind("Quality", nameof(ShadowDistance), "KeepOriginal",
                "Override shadow draw distance in world units: KeepOriginal (default) or a value like 40 / 30 / 25.");
            ShadowResolution = File.Bind("Quality", nameof(ShadowResolution), "KeepOriginal",
                "Override shadow resolution: KeepOriginal (default) or Low | Medium | High | VeryHigh.");
            LodBias = File.Bind("Quality", nameof(LodBias), "KeepOriginal",
                "Override LOD bias: KeepOriginal (default) or a value like 0.8 / 0.7 / 0.6 (lower = coarser LODs).");
            MSAA = File.Bind("Quality", nameof(MSAA), "KeepOriginal",
                "Override multisample anti-aliasing: KeepOriginal (default) or 0 | 2 | 4 | 8.");

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

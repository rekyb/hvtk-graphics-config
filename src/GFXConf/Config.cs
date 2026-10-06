using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

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

    /// <summary>
    /// Toggle entries keyed by the concrete settings type name, for the PPv2
    /// and SCPE sweep groups. The 3 Volumetrics toggles are component groups
    /// (name lookup in the sweep), not settings types — intentionally absent.
    /// </summary>
    internal static IReadOnlyDictionary<string, ConfigEntry<bool>> SettingToggles { get; private set; }
        = new Dictionary<string, ConfigEntry<bool>>();

    /// <summary>
    /// Binds all 18 entries to &lt;game&gt;/BepInEx/config/gfxconf.cfg and saves
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

            OverrideMode = File.Bind("Antialiasing", nameof(OverrideMode), "KeepOriginal", "PostProcessLayer antialiasing override: None | FastFXAA | FXAA | SMAA | TAA, or KeepOriginal to leave the game default.");

            ReapplyOnSceneLoad = File.Bind("General", nameof(ReapplyOnSceneLoad), true, "Re-run the effect sweep after each scene load.");
            DelaySeconds = File.Bind("General", nameof(DelaySeconds), 2, "Seconds to wait after a scene load before the sweep runs.");
            EnableF10Overlay = File.Bind("General", nameof(EnableF10Overlay), true, "Enable the F10 in-game settings overlay.");

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

    /// <summary>Persists the config file (overlay edits). Never throws.</summary>
    internal static void Save()
    {
        try
        {
            File?.Save();
        }
        catch (Exception ex)
        {
            LogSource?.LogWarning($"[GFXConf] config save failed: {ex}");
        }
    }
}

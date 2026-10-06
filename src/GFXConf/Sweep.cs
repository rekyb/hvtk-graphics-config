using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace GFXConf;

/// <summary>
/// Deferred sweep scheduler (spec §4.1: one schedule line + one summary line
/// per sweep, never per-frame logging).
/// <list type="bullet">
/// <item><see cref="Request"/> — schedule/coalesce (newest pending wins)</item>
/// <item><see cref="Tick"/> — called every frame from <c>GfxBehaviour.Update</c></item>
/// <item><see cref="RunNow"/> — executes when due: PPv2 + SCPE settings
/// sweep, component disables (VolumetricFog / planar / aura) and the AA
/// override, all with real counts in one summary line.</item>
/// </list>
/// </summary>
internal static class Sweeper
{
    private static bool _pending;
    private static string _sceneLabel;
    private static float _dueTime;

    /// <summary>
    /// Component type full names, verified against the shipped interop DLLs
    /// (spec §3): <c>VolumetricFogAndMist.VolumetricFog</c> and
    /// <c>Ceto.PlanarReflection</c> live in Assembly-CSharp-firstpass.dll;
    /// Aura2's classes are namespaced <c>Aura2API</c> (NOT <c>Aura2</c>) in
    /// Aura2_Core.dll — interop is authoritative per the task contract.
    /// </summary>
    private const string VolumetricFogType = "VolumetricFogAndMist.VolumetricFog";
    private const string PlanarReflectionType = "Ceto.PlanarReflection";

    /// <summary>The three Aura2 types counted together under the `aura` label.</summary>
    private static readonly string[] AuraTypes =
    {
        "Aura2API.Aura",
        "Aura2API.AuraVolume",
        "Aura2API.AuraCamera"
    };

    /// <summary>
    /// One-shot component type cache (spec §4.3 "log once, skip silently"):
    /// full name → resolved type, with MISSING names cached as null too, so a
    /// failed lookup is never re-scanned on later sweeps and its
    /// `[GFXConf] type not found: {fullName}` warning is logged exactly once
    /// per session.
    /// </summary>
    private static readonly Dictionary<string, Type> _typeCache = new();

    /// <summary>
    /// Original <c>active</c> and <c>enabled.value</c> captured on the first
    /// modification of a settings instance (keyed by the wrapper instance —
    /// Il2CppInterop pools wrappers per native pointer, and this strong
    /// reference keeps the key stable across sweeps while an effect is held
    /// disabled). Release runs ONLY for entries present here (never-disabled
    /// effects are left untouched); entries are removed on release so a later
    /// disable recaptures freshly.
    /// </summary>
    private static readonly Dictionary<PostProcessEffectSettings, (bool Active, bool Value)> _originalActive = new();

    /// <summary>
    /// Logs the schedule line, then stores label + due time. A newer Request
    /// replaces any pending one (coalescing — newest wins).
    /// </summary>
    internal static void Request(string sceneLabel, float delaySeconds)
    {
        GfxConfig.LogSource?.LogInfo($"[GFXConf] scene={sceneLabel} sweep in {delaySeconds}s");
        _sceneLabel = sceneLabel;
        _dueTime = Time.realtimeSinceStartup + delaySeconds;
        _pending = true;
    }

    /// <summary>
    /// Per-frame pump: fires the pending sweep once its due time is reached,
    /// clearing the pending flag before running so a throwing sweep cannot
    /// re-fire every frame. Does nothing when nothing is scheduled.
    /// </summary>
    internal static void Tick()
    {
        if (!_pending || Time.realtimeSinceStartup < _dueTime)
        {
            return;
        }

        var label = _sceneLabel;
        _pending = false;
        _sceneLabel = null;
        RunNow(label);
    }

    /// <summary>
    /// Executes the sweep for <paramref name="sceneLabel"/>: the settings
    /// group, the three component groups (VolumetricFog / planar / aura) and
    /// the AA override each run in their own try/catch (failures become one
    /// warning), then exactly ONE summary line is logged (pinned 15-label
    /// format with real counts). Full outer try/catch: failures become one
    /// warning, never rethrown.
    /// </summary>
    internal static void RunNow(string sceneLabel)
    {
        try
        {
            // Per-label counts for THIS sweep (11 settings + 4 component/AA).
            var counts = new Dictionary<string, int>
            {
                ["AmbientOcclusion"] = 0,
                ["ChromaticAberration"] = 0,
                ["DepthOfField"] = 0,
                ["ScreenSpaceReflections"] = 0,
                ["MotionBlur"] = 0,
                ["Bloom"] = 0,
                ["Fog"] = 0,
                ["CloudShadows"] = 0,
                ["AmbientOcclusion2D"] = 0,
                ["Blur"] = 0,
                ["Sharpen"] = 0,
                ["VolumetricFog"] = 0,
                ["planar"] = 0,
                ["aura"] = 0,
                ["aa"] = 0
            };

            // Settings group (spec §4.1): own try/catch — a failure warns and
            // the summary below still logs with whatever was counted so far.
            try
            {
                var toggles = GfxConfig.SettingToggles;
                var profiles = Resources.FindObjectsOfTypeAll<PostProcessProfile>();
                if (profiles != null)
                {
                    foreach (var profile in profiles)
                    {
                        if (profile == null)
                        {
                            continue; // null or destroyed profile
                        }

                        var settings = profile.settings;
                        if (settings == null)
                        {
                            continue;
                        }

                        var settingsCount = settings.Count;
                        for (var i = 0; i < settingsCount; i++)
                        {
                            var effect = settings[i];
                            if (effect == null)
                            {
                                continue; // null or destroyed entry
                            }

                            var typeName = ConcreteTypeName(effect);
                            if (typeName == null || !toggles.TryGetValue(typeName, out var toggle))
                            {
                                continue; // not one of the 11 keyed settings — never touched
                            }

                            if (toggle.Value)
                            {
                                // Disable: capture stock `active` AND stock
                                // `enabled.value` on FIRST modification only,
                                // then force the effect off.
                                if (!_originalActive.ContainsKey(effect))
                                {
                                    _originalActive[effect] = (effect.active, effect.enabled.value);
                                }

                                effect.active = false;
                                effect.enabled.value = false;
                                effect.enabled.overrideState = true;
                                counts[typeName]++;
                            }
                            else if (_originalActive.TryGetValue(effect, out var original))
                            {
                                // Release (Review Focus 2): ONLY effects this
                                // plugin disabled this session are touched —
                                // never-disabled settings are left completely
                                // untouched. Restore stock `active` AND stock
                                // `enabled.value` exactly as captured, then
                                // drop the override, then forget.
                                effect.active = original.Active;
                                effect.enabled.value = original.Value;
                                effect.enabled.overrideState = false;
                                _originalActive.Remove(effect);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] settings sweep failed (scene={sceneLabel}): {ex}");
            }

            // Component groups (spec §4.1): each in its OWN try/catch — a
            // failure warns and the summary below still logs with whatever
            // was counted so far. Disable-only (ruling T5 pre): a false
            // toggle writes nothing (no component restore — components
            // re-enable on restart).
            try
            {
                DisableComponents(counts, "VolumetricFog", GfxConfig.DisableVolumetricFog?.Value == true,
                    new[] { VolumetricFogType });
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] component sweep failed (scene={sceneLabel}, VolumetricFog): {ex}");
            }

            try
            {
                DisableComponents(counts, "planar", GfxConfig.DisablePlanarReflections?.Value == true,
                    new[] { PlanarReflectionType });
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] component sweep failed (scene={sceneLabel}, planar): {ex}");
            }

            try
            {
                DisableComponents(counts, "aura", GfxConfig.DisableAura2?.Value == true, AuraTypes);
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] component sweep failed (scene={sceneLabel}, aura): {ex}");
            }

            // AA override (spec §4.1/§4.3): own try/catch.
            try
            {
                ApplyAntialiasingOverride(counts);
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] aa sweep failed (scene={sceneLabel}): {ex}");
            }

            GfxConfig.LogSource?.LogInfo($"[GFXConf] scene={sceneLabel}: AO={counts["AmbientOcclusion"]}, CA={counts["ChromaticAberration"]}, DoF={counts["DepthOfField"]}, SSR={counts["ScreenSpaceReflections"]}, MB={counts["MotionBlur"]}, Bloom={counts["Bloom"]}, SCPE.Fog={counts["Fog"]}, SCPE.CloudShadows={counts["CloudShadows"]}, SCPE.AO2D={counts["AmbientOcclusion2D"]}, SCPE.Blur={counts["Blur"]}, SCPE.Sharpen={counts["Sharpen"]}, VolumetricFog={counts["VolumetricFog"]}, planar={counts["planar"]}, aura={counts["aura"]}, aa={counts["aa"]}");
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] sweep failed (scene={sceneLabel}): {ex}");
        }
    }

    /// <summary>
    /// Resolves <paramref name="fullName"/> across the loaded assemblies
    /// (spec §4.1 name lookup), at most ONCE per session: hits and misses are
    /// both cached, and a miss logs exactly one
    /// <c>[GFXConf] type not found: {fullName}</c> warning ever before the
    /// name is skipped silently on all later sweeps. Never throws — an
    /// unreadable assembly produces one warning and is skipped.
    /// </summary>
    private static Type ResolveType(string fullName)
    {
        if (_typeCache.TryGetValue(fullName, out var cached))
        {
            return cached; // hit OR cached miss (null) — never re-scan
        }

        Type resolved = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                resolved = assembly.GetType(fullName);
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] type lookup scan failed ({fullName}): {ex.Message}");
                continue;
            }

            if (resolved != null)
            {
                break;
            }
        }

        _typeCache[fullName] = resolved; // cache the miss too
        if (resolved == null)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] type not found: {fullName}");
        }

        return resolved;
    }

    /// <summary>
    /// Component group (spec §4.1): resolve each type once (session cache),
    /// run the non-generic <c>FindObjectsOfType(type)</c>, and for every
    /// result that is a <c>Behaviour</c> force <c>enabled = false</c> and
    /// count it while the group's config toggle is true. A false (or
    /// unbound) toggle returns before any lookup or write — components are
    /// disable-only (ruling T5 pre: they re-enable on restart).
    /// </summary>
    private static void DisableComponents(
        Dictionary<string, int> counts, string countKey, bool disable, string[] typeNames)
    {
        if (!disable)
        {
            return; // toggle false → do nothing (no component restore)
        }

        foreach (var fullName in typeNames)
        {
            var type = ResolveType(fullName);
            if (type == null)
            {
                continue; // missing type — logged once, cached, skipped
            }

            var found = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(type));
            if (found == null)
            {
                continue;
            }

            foreach (var obj in found)
            {
                if (obj == null)
                {
                    continue; // null or destroyed result
                }

                // Il2CppInterop pools wrappers as the STATIC requested type
                // (proven in Task 4), so a result wrapped as the base
                // UnityEngine.Object may fail `is Behaviour` even when the
                // native object IS one — re-wrap as the resolved type first.
                // `as Behaviour` still enforces "only Behaviours are touched"
                // (e.g. Aura2API.Aura is a plain il2cpp object, not one).
                var behaviour = obj as Behaviour
                    ?? (Activator.CreateInstance(type, obj.Pointer) as Behaviour);
                if (behaviour == null)
                {
                    continue;
                }

                behaviour.enabled = false;
                counts[countKey]++;
            }
        }
    }

    /// <summary>
    /// AA override (spec §4.1): nothing to do for <c>KeepOriginal</c> (or a
    /// missing entry — touch nothing); otherwise the value is parsed ONCE per
    /// sweep, an unparseable value logs exactly one
    /// <c>[GFXConf] invalid OverrideMode: {value}</c> warning and leaves all
    /// layers untouched, and each found <c>PostProcessLayer</c> gets
    /// <c>antialiasingMode</c> assigned and counted into <c>aa</c>.
    /// </summary>
    private static void ApplyAntialiasingOverride(Dictionary<string, int> counts)
    {
        var value = GfxConfig.OverrideMode?.Value;
        if (string.IsNullOrEmpty(value) || value == "KeepOriginal")
        {
            return; // keep the game default — touch nothing
        }

        if (!TryParseAntialiasingMode(value, out var mode))
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] invalid OverrideMode: {value}");
            return;
        }

        var layers = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.From(typeof(PostProcessLayer)));
        if (layers == null)
        {
            return;
        }

        foreach (var obj in layers)
        {
            if (obj == null)
            {
                continue; // null or destroyed layer
            }

            // Same static-wrapper re-wrap as the component groups.
            var layer = obj as PostProcessLayer
                ?? (Activator.CreateInstance(typeof(PostProcessLayer), obj.Pointer) as PostProcessLayer);
            if (layer == null)
            {
                continue;
            }

            layer.antialiasingMode = mode;
            counts["aa"]++;
        }
    }

    /// <summary>
    /// Parses <c>OverrideMode</c> into this game's PPv2 enum — nested
    /// <c>PostProcessLayer.Antialiasing</c> (the plan/spec prose name
    /// `AntialiasingMode` exists in NO interop assembly; members are None /
    /// FastApproximateAntialiasing / SubpixelMorphologicalAntialiasing /
    /// TemporalAntialiasing — verified against Unity.Postprocessing.Runtime).
    /// DEVIATION (spec §4.2 forced): §4.2 documents the domain
    /// `None | FastFXAA | FXAA | SMAA | TAA`, but those short names are NOT
    /// enum members — plain <c>Enum.TryParse</c> alone would reject every
    /// alias (spec test 4 sets FXAA), so the documented short names are
    /// normalized to members first; <c>Enum.TryParse</c> then accepts full
    /// member names verbatim. Unknown strings return false → one warning.
    /// </summary>
    private static bool TryParseAntialiasingMode(string value, out PostProcessLayer.Antialiasing mode)
    {
        if (Enum.TryParse(value, true, out mode))
        {
            return true; // full member name (e.g. "TemporalAntialiasing")
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "fxaa":
            case "fastfxaa":
                mode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
                return true;
            case "smaa":
                mode = PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
                return true;
            case "taa":
                mode = PostProcessLayer.Antialiasing.TemporalAntialiasing;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    /// <summary>
    /// Concrete settings type name for the <c>SettingToggles</c> lookup
    /// (spec §4.1 "match concrete type name"). Prefers
    /// <c>GetType().Name</c>; however Il2CppInterop wraps objects as the
    /// STATIC requested type (verified against the shipped Il2CppInterop
    /// 1.5.3: <c>List&lt;T&gt;.get_Item</c> → <c>PointerToValueGeneric&lt;T&gt;</c> →
    /// <c>new T</c>), so for a base-typed wrapper the native il2cpp class
    /// name is the authoritative concrete name (e.g. "Bloom", "Fog").
    /// </summary>
    private static string ConcreteTypeName(PostProcessEffectSettings effect)
    {
        var name = effect.GetType().Name;
        if (name != nameof(PostProcessEffectSettings))
        {
            return name;
        }

        var nativeClass = effect.ObjectClass;
        return nativeClass == IntPtr.Zero
            ? null
            : Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name_(nativeClass);
    }
}

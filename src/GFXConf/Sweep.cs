using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
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
/// <item><see cref="Reapply"/> — per-frame hold: re-forces the disabled
/// settings off after the game re-applies its profiles (see §4.1).</item>
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

    /// <summary>
    /// The two Aura2 types counted together under the `aura` label.
    /// <c>Aura2API.Aura</c> is deliberately NOT listed: runtime proved it is
    /// a plain <c>Il2CppSystem.Object</c> (not a UnityEngine.Object), so
    /// passing it to FindObjectsOfType makes Unity log
    /// <c>FindAllObjectsOfType: The type has to be derived from
    /// UnityEngine.Object. Type is Aura.</c> on every sweep.
    /// </summary>
    private static readonly string[] AuraTypes =
    {
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
    /// Captured stock <c>QualitySettings</c> values for the [Quality]
    /// overrides, keyed by setting name. Present only while that setting is
    /// overridden this session; removed on restore so a later override
    /// recaptures the (possibly changed) stock value.
    /// </summary>
    private static readonly Dictionary<string, object> _qualityOriginals = new();

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
    /// Per-frame hold (v0.4.0): guards the disabled effects against the game
    /// re-applying its post-process profiles during play (its weather/season
    /// system rewrites them at runtime). Re-force every effect this plugin has
    /// disabled back off, from the already-captured instances (no rescan).
    /// Called from <c>GfxBehaviour.LateUpdate</c> so the write lands after the
    /// game's Update and before the frame renders. Idempotent: only writes when
    /// a held effect is (partly) on again. Never throws.
    /// </summary>
    internal static void Reapply()
    {
        if (_originalActive.Count == 0)
        {
            return;
        }

        foreach (var effect in _originalActive.Keys)
        {
            try
            {
                if (effect == null)
                {
                    continue; // destroyed wrapper
                }

                if (effect.active || effect.enabled.value || !effect.enabled.overrideState)
                {
                    effect.active = false;
                    DisableEnabled(effect);
                }
            }
            catch
            {
                // transient/destroyed instance — skip; a later sweep recaptures
            }
        }
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
            // Crash-safety test hook (spec test 7): MUST stay false in
            // committed code (kept as dead code by design). When true, the
            // deliberate throw below is caught by the outer catch → one
            // warning, never an unhandled crash.
            const bool ForceTestException = false;
            if (ForceTestException)
            {
                throw new InvalidOperationException("[GFXConf] forced test exception");
            }

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
                ["aa"] = 0,
                ["quality"] = 0
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
                                DisableEnabled(effect);
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

            // Quality overrides (v0.4.0): own try/catch.
            try
            {
                ApplyQualityOverrides(counts);
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] quality sweep failed (scene={sceneLabel}): {ex}");
            }

            var summary = $"[GFXConf] scene={sceneLabel}: AO={counts["AmbientOcclusion"]}, CA={counts["ChromaticAberration"]}, DoF={counts["DepthOfField"]}, SSR={counts["ScreenSpaceReflections"]}, MB={counts["MotionBlur"]}, Bloom={counts["Bloom"]}, SCPE.Fog={counts["Fog"]}, SCPE.CloudShadows={counts["CloudShadows"]}, SCPE.AO2D={counts["AmbientOcclusion2D"]}, SCPE.Blur={counts["Blur"]}, SCPE.Sharpen={counts["Sharpen"]}, VolumetricFog={counts["VolumetricFog"]}, planar={counts["planar"]}, aura={counts["aura"]}, aa={counts["aa"]}, quality={counts["quality"]}";
            GfxConfig.LogSource?.LogInfo(summary);
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
                // (the requested type itself may be a plain il2cpp object —
                // e.g. the excluded Aura2API.Aura — in which case the re-wrap
                // yields null and nothing is touched).
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
    /// <c>antialiasingMode</c> assigned (plus <c>fastMode</c> for the
    /// FastFXAA/FXAA aliases — controller amendment) and counted into
    /// <c>aa</c> exactly once per layer.
    /// </summary>
    private static void ApplyAntialiasingOverride(Dictionary<string, int> counts)
    {
        var value = GfxConfig.OverrideMode?.Value;
        if (string.IsNullOrEmpty(value) || value == "KeepOriginal")
        {
            return; // keep the game default — touch nothing
        }

        if (!TryParseAntialiasingMode(value, out var mode, out var fastMode))
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

            // Controller amendment: FastFXAA/FXAA set fastMode distinctly
            // (True/False); None/SMAA/TAA — and full enum member names —
            // leave fastMode untouched. fastMode is a plain bool on this
            // build's FastApproximateAntialiasing settings instance.
            if (fastMode.HasValue)
            {
                var faa = layer.fastApproximateAntialiasing;
                if (faa != null)
                {
                    faa.fastMode = fastMode.Value;
                }
            }

            counts["aa"]++; // exactly once per layer, regardless of fields written
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
    /// <para>
    /// Controller amendment — fastMode semantics: <c>FastFXAA</c> → mode +
    /// <c>fastMode=true</c>; <c>FXAA</c> → mode + <c>fastMode=false</c>;
    /// <c>None</c>/<c>SMAA</c>/<c>TAA</c> (and full enum member names) →
    /// mode ONLY, <paramref name="fastMode"/> stays null (untouched).
    /// </para>
    /// </summary>
    private static bool TryParseAntialiasingMode(
        string value, out PostProcessLayer.Antialiasing mode, out bool? fastMode)
    {
        fastMode = null;
        if (Enum.TryParse(value, true, out mode))
        {
            return true; // full member name (e.g. "TemporalAntialiasing") — mode only
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "fastfxaa":
                mode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
                fastMode = true;
                return true;
            case "fxaa":
                mode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
                fastMode = false;
                return true;
            case "smaa":
                mode = PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
                return true;
            case "taa":
                mode = PostProcessLayer.Antialiasing.TemporalAntialiasing;
                return true;
            default:
                mode = default;
                fastMode = null;
                return false;
        }
    }

    /// <summary>
    /// Applies the four [Quality] overrides (spec §4.2, v0.4.0). Each setting
    /// is independent: "KeepOriginal" (or empty) restores the captured stock
    /// value and drops the override; otherwise the value is parsed and, on
    /// first application, the stock QualitySettings value is captured so a
    /// later restore is exact. Invalid values log one warning and are skipped.
    /// The active-override count is written into <c>counts["quality"]</c>.
    /// </summary>
    private static void ApplyQualityOverrides(Dictionary<string, int> counts)
    {
        var active = 0;
        active += ApplyShadowDistance() ? 1 : 0;
        active += ApplyShadowResolution() ? 1 : 0;
        active += ApplyLodBias() ? 1 : 0;
        active += ApplyMsaa() ? 1 : 0;
        counts["quality"] = active;
    }

    private static bool ApplyShadowDistance()
    {
        var value = GfxConfig.ShadowDistance?.Value;
        if (IsKeepOriginal(value))
        {
            RestoreQuality("ShadowDistance");
            return false;
        }

        if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var distance))
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] invalid ShadowDistance: {value}");
            return false;
        }

        CaptureAndLog("ShadowDistance", QualitySettings.shadowDistance, distance.ToString(CultureInfo.InvariantCulture));
        QualitySettings.shadowDistance = distance;
        return true;
    }

    private static bool ApplyShadowResolution()
    {
        var value = GfxConfig.ShadowResolution?.Value;
        if (IsKeepOriginal(value))
        {
            RestoreQuality("ShadowResolution");
            return false;
        }

        if (!Enum.TryParse(value.Trim(), true, out ShadowResolution resolution))
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] invalid ShadowResolution: {value}");
            return false;
        }

        CaptureAndLog("ShadowResolution", QualitySettings.shadowResolution, resolution.ToString());
        QualitySettings.shadowResolution = resolution;
        return true;
    }

    private static bool ApplyLodBias()
    {
        var value = GfxConfig.LodBias?.Value;
        if (IsKeepOriginal(value))
        {
            RestoreQuality("LodBias");
            return false;
        }

        if (!float.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var bias))
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] invalid LodBias: {value}");
            return false;
        }

        CaptureAndLog("LodBias", QualitySettings.lodBias, bias.ToString(CultureInfo.InvariantCulture));
        QualitySettings.lodBias = bias;
        return true;
    }

    private static bool ApplyMsaa()
    {
        var value = GfxConfig.MSAA?.Value;
        if (IsKeepOriginal(value))
        {
            RestoreQuality("MSAA");
            return false;
        }

        if (!int.TryParse(value.Trim(), out var msaa) || (msaa != 0 && msaa != 2 && msaa != 4 && msaa != 8))
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] invalid MSAA: {value}");
            return false;
        }

        CaptureAndLog("MSAA", QualitySettings.antiAliasing, msaa.ToString(CultureInfo.InvariantCulture));
        QualitySettings.antiAliasing = msaa;
        return true;
    }

    /// <summary>True when a [Quality] value means "leave the game default alone".</summary>
    private static bool IsKeepOriginal(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            || string.Equals(value.Trim(), "KeepOriginal", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Captures the stock value on the FIRST application of an override (so a
    /// later KeepOriginal restores it exactly) and logs the applied value
    /// exactly once per session per setting — no per-sweep spam.
    /// </summary>
    private static void CaptureAndLog(string key, object original, string applied)
    {
        if (_qualityOriginals.ContainsKey(key))
        {
            return;
        }

        _qualityOriginals[key] = original;
        GfxConfig.LogSource?.LogInfo($"[GFXConf] quality override: {key}={applied}");
    }

    /// <summary>Restores the captured stock value for <paramref name="key"/> and forgets it.</summary>
    private static void RestoreQuality(string key)
    {
        if (!_qualityOriginals.TryGetValue(key, out var original))
        {
            return;
        }

        switch (key)
        {
            case "ShadowDistance":
                QualitySettings.shadowDistance = (float)original;
                break;
            case "ShadowResolution":
                QualitySettings.shadowResolution = (ShadowResolution)original;
                break;
            case "LodBias":
                QualitySettings.lodBias = (float)original;
                break;
            case "MSAA":
                QualitySettings.antiAliasing = (int)original;
                break;
        }

        _qualityOriginals.Remove(key);
    }

    /// <summary>
    /// Forces an effect's <c>enabled</c> override off. The interop-generated
    /// <c>ParameterOverride&lt;T&gt;.value</c> property SETTER is a silent
    /// no-op on this build (the getter works), so this calls the native
    /// <c>Override(bool)</c> method and, if that does not land, writes the
    /// il2cpp field directly by offset (the same write the native setter
    /// performs). Never throws.
    /// </summary>
    private static void DisableEnabled(PostProcessEffectSettings effect)
    {
        var enabled = effect.enabled;
        if (enabled == null)
        {
            return;
        }

        try
        {
            enabled.Override(false);
            if (!enabled.value)
            {
                return;
            }
        }
        catch
        {
            // fall through to the direct field write
        }

        try
        {
            var field = FindField(enabled.ObjectClass, "value");
            if (field != IntPtr.Zero)
            {
                var offset = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
                Marshal.WriteByte(IntPtr.Add(enabled.Pointer, offset), 0);
            }
        }
        catch
        {
            // give up — the effect simply stays on for this instance
        }
    }

    /// <summary>
    /// Resolves a field by name walking the class hierarchy (<c>value</c>
    /// lives on the generic base <c>ParameterOverride&lt;T&gt;</c>, not on
    /// <c>BoolParameter</c>). Returns <c>IntPtr.Zero</c> when absent.
    /// </summary>
    private static IntPtr FindField(IntPtr klass, string name)
    {
        while (klass != IntPtr.Zero)
        {
            var field = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_field_from_name(klass, name);
            if (field != IntPtr.Zero)
            {
                return field;
            }

            klass = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_parent(klass);
        }

        return IntPtr.Zero;
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

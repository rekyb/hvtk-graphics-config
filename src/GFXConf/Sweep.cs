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
/// sweep with real counts (component groups + AA stay 0 until Task 5).</item>
/// </list>
/// </summary>
internal static class Sweeper
{
    private static bool _pending;
    private static string _sceneLabel;
    private static float _dueTime;

    /// <summary>
    /// Original <c>active</c> captured on the first modification of a settings
    /// instance (keyed by the wrapper instance — Il2CppInterop pools wrappers
    /// per native pointer, and this strong reference keeps the key stable
    /// across sweeps while an effect is held disabled).
    /// Entries are removed on release so a later disable recaptures freshly.
    /// </summary>
    private static readonly Dictionary<PostProcessEffectSettings, bool> _originalActive = new();

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
    /// group runs in its own try/catch (failures become one warning), then
    /// exactly ONE summary line is logged (pinned format — VolumetricFog,
    /// planar, aura, aa stay 0 until Task 5). Full outer try/catch: failures
    /// become one warning, never rethrown.
    /// </summary>
    internal static void RunNow(string sceneLabel)
    {
        try
        {
            // Per-label counts for THIS sweep (all 11 keyed settings start at 0).
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
                ["Sharpen"] = 0
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
                                // Disable: capture stock `active` on FIRST
                                // modification only, then force the effect off.
                                if (!_originalActive.ContainsKey(effect))
                                {
                                    _originalActive[effect] = effect.active;
                                }

                                effect.active = false;
                                effect.enabled.value = false;
                                effect.enabled.overrideState = true;
                                counts[typeName]++;
                            }
                            else
                            {
                                // Release (Review Focus 2): drop the override
                                // and restore stock `active` exactly as
                                // captured (fallback if uncaptured: true).
                                effect.enabled.overrideState = false;
                                effect.active = _originalActive.TryGetValue(effect, out var originalActive)
                                    ? originalActive
                                    : true;
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

            GfxConfig.LogSource?.LogInfo($"[GFXConf] scene={sceneLabel}: AO={counts["AmbientOcclusion"]}, CA={counts["ChromaticAberration"]}, DoF={counts["DepthOfField"]}, SSR={counts["ScreenSpaceReflections"]}, MB={counts["MotionBlur"]}, Bloom={counts["Bloom"]}, SCPE.Fog={counts["Fog"]}, SCPE.CloudShadows={counts["CloudShadows"]}, SCPE.AO2D={counts["AmbientOcclusion2D"]}, SCPE.Blur={counts["Blur"]}, SCPE.Sharpen={counts["Sharpen"]}, VolumetricFog=0, planar=0, aura=0, aa=0");
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] sweep failed (scene={sceneLabel}): {ex}");
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

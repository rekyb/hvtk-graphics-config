using System;
using BepInEx.Configuration;
using UnityEngine;

namespace GFXConf;

/// <summary>
/// Hidden IL2CPP-injected pump component on a DontDestroyOnLoad GameObject.
/// Owns the plugin's per-frame work: <c>Update()</c> ticks the sweep
/// scheduler (spec §4.1) and toggles the F10 overlay; <c>OnGUI()</c> draws
/// the overlay window — one control per §4.2 config entry, grouped by
/// section. The GUI only reads/writes <see cref="GfxConfig"/> entries: on
/// any change it saves the cfg and schedules an immediate sweep (spec §4.1
/// step 4). Drawing costs nothing while the overlay is closed.
/// </summary>
internal sealed class GfxBehaviour : MonoBehaviour
{
    private static bool _registered;
    private static bool _created;

    /// <summary>IMGUI window id (contract-pinned) for <see cref="GUILayout.Window"/>.</summary>
    private const int WindowId = 4170;

    // Overlay state is static on purpose: EnsureCreated() guarantees a
    // single pump instance, and statics are proven to work on this injected
    // type (the _registered/_created flags already rely on it).

    /// <summary>
    /// Overlay visibility — F10 always closes it; opening requires
    /// <c>EnableF10Overlay = true</c> (gate applies to opening only).
    /// </summary>
    private static bool _visible;

    /// <summary>Initial window position/size for the GUILayout.Window path.</summary>
    private static Rect _windowRect = new(20f, 20f, 360f, 520f);

    /// <summary>
    /// Set once <c>GUILayout.Window</c> proves unusable in this game build:
    /// its internal <c>LayoutedWindow</c> class was stripped from the shipped
    /// il2cpp metadata (verified: the type name is absent from
    /// <c>global-metadata.dat</c> while the interop still declares it →
    /// <c>MissingMethodException: LayoutedWindow..ctor</c> on every call —
    /// known BepInEx issue, closed not-planned). The overlay then draws a
    /// fixed panel with <c>GUI.Box</c> + <c>GUILayout.BeginArea</c> instead,
    /// which avoids the stripped type entirely. Sticky for the session.
    /// </summary>
    private static bool _panelMode;

    /// <summary>
    /// Latched when F10 handling itself throws (e.g. input backend missing):
    /// a persistent failure must not warn every frame (rules §2.3).
    /// </summary>
    private static bool _f10Broken;

    /// <summary>
    /// Fixed panel geometry for the fallback path (position/size are fixed
    /// per contract — content is laid out inside <see cref="BodyRect"/>).
    /// </summary>
    private static readonly Rect PanelRect = new(16f, 16f, 360f, 620f);

    /// <summary>Title strip drawn on top of the panel background.</summary>
    private static readonly Rect HeaderRect = new(16f, 16f, 360f, 26f);

    /// <summary>Where the shared GUILayout content is laid out in panel mode.</summary>
    private static readonly Rect BodyRect = new(18f, 44f, 356f, 590f);

    /// <summary>Bold section header style, created once on first draw.</summary>
    private static GUIStyle _sectionStyle;

    /// <summary>Word-wrapping label style for the long summary line.</summary>
    private static GUIStyle _wrapStyle;

    /// <summary>
    /// The <c>OverrideMode</c> cycle (spec §4.2 domain) in contract order:
    /// KeepOriginal → None → FastFXAA → FXAA → SMAA → TAA → wrap.
    /// </summary>
    private static readonly string[] OverrideCycle =
    {
        "KeepOriginal", "None", "FastFXAA", "FXAA", "SMAA", "TAA"
    };

    /// <summary>
    /// Cached window callback (rules §3 "no allocations in hot paths"):
    /// <see cref="GUILayout.Window"/>'s parameter is an interop (il2cpp)
    /// delegate type that a C# method group does not convert to directly,
    /// so the conversion goes through a System delegate — created once here.
    /// </summary>
    private static readonly Action<int> WindowFunc = DrawWindow;

    /// <summary>
    /// Required: Il2CppInterop's ClassInjector instantiates injected types
    /// through a public parameterless constructor. Do not remove or change.
    /// </summary>
    public GfxBehaviour()
    {
    }

    /// <summary>
    /// Idempotent setup: registers the type with il2cpp exactly once, then
    /// creates the persistent "GFXConf" GameObject and attaches this
    /// component. Re-entrant: if the game destroys the pump (observed during
    /// the boot-time scene transition), the next call rebuilds it. Never
    /// throws — failures become one warning.
    /// </summary>
    internal static void EnsureCreated()
    {
        try
        {
            if (!_registered)
            {
                Il2CppInterop.Runtime.Injection.ClassInjector.RegisterTypeInIl2Cpp<GfxBehaviour>();
                _registered = true;
            }

            if (!_created)
            {
                var go = new GameObject("GFXConf");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<GfxBehaviour>();
                _created = true;
            }
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] behaviour pump setup failed: {ex}");
        }
    }

    private void Update()
    {
        try
        {
            Sweeper.Tick();
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] pump tick failed: {ex}");
        }

        // F10 toggle (contract amendment — gate exempts CLOSING): the key is
        // ALWAYS read (no gate before GetKeyDown). An open overlay closes on
        // F10 regardless of EnableF10Overlay, so unchecking the toggle while
        // the overlay is open can never lock the user out of closing it. Only
        // OPENING is gated: EnableF10Overlay false (or null — Bind failed,
        // already warned once) → F10 does nothing while closed, no log line.
        // GetKeyDown is only read once per frame, and any thrown failure
        // latches F10 off for the session so a persistent input error cannot
        // warn every frame.
        if (_f10Broken)
        {
            return;
        }

        try
        {
            if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F10))
            {
                if (_visible)
                {
                    _visible = false;
                    GfxConfig.LogSource?.LogInfo("[GFXConf] overlay closed");
                }
                else
                {
                    var gate = GfxConfig.EnableF10Overlay;
                    if (gate != null && gate.Value)
                    {
                        _visible = true;
                        GfxConfig.LogSource?.LogInfo("[GFXConf] overlay opened");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _f10Broken = true;
            GfxConfig.LogSource?.LogWarning($"[GFXConf] F10 handling disabled: {ex}");
        }
    }

    /// <summary>
    /// Drawing only (rules §3): early-returns while hidden so a closed
    /// overlay costs one bool check per frame. The whole visible body runs
    /// in try/catch — a failure logs ONE warning and closes the overlay
    /// (never rethrows, and never warns per frame → the game UI keeps
    /// working either way).
    /// <para>
    /// Window call order: contract-pinned <c>GUILayout.Window</c> first; if
    /// this build cannot provide it (stripped <c>LayoutedWindow</c>, see
    /// <see cref="_panelMode"/>), fall back to a fixed GUI.Box panel that
    /// hosts the exact same content.
    /// </para>
    /// </summary>
    private void OnGUI()
    {
        if (!_visible)
        {
            return;
        }

        try
        {
            if (!_panelMode)
            {
                try
                {
                    _windowRect = GUILayout.Window(WindowId, _windowRect, WindowFunc, "GFXConf");
                    return;
                }
                catch (Exception ex)
                {
                    _panelMode = true;
                    GfxConfig.LogSource?.LogWarning(
                        $"[GFXConf] GUILayout.Window unavailable — overlay uses fixed panel: {ex.Message}");
                }
            }

            DrawPanel();
        }
        catch (Exception ex)
        {
            _visible = false;
            GfxConfig.LogSource?.LogWarning($"[GFXConf] overlay draw failed: {ex}");
        }
    }

    /// <summary>
    /// Fixed-panel fallback: window-style background box, the shared
    /// GUILayout content in <see cref="BodyRect"/>, then the title strip.
    /// Begin/EndArea is protected by finally so a throwing control can
    /// never leave the global layout stack unbalanced (it would corrupt
    /// every later GUILayout draw, including the game's own).
    /// </summary>
    private static void DrawPanel()
    {
        GUI.Box(PanelRect, string.Empty, GUI.skin.window);
        GUILayout.BeginArea(BodyRect);
        try
        {
            DrawContent();
        }
        finally
        {
            GUILayout.EndArea();
        }

        GUI.Box(HeaderRect, "GFXConf overlay", GUI.skin.box);
    }

    /// <summary>
    /// <c>GUILayout.Window</c> callback. In the IL2CPP trampoline a thrown
    /// exception never reaches <c>OnGUI</c>'s catch (it is swallowed one
    /// frame deeper and logged by Il2CppInterop as an error), so the content
    /// guards itself: a failure warns once, switches to the panel path and
    /// draws nothing for this event.
    /// </summary>
    private static void DrawWindow(int id)
    {
        try
        {
            DrawContent();
        }
        catch (Exception ex)
        {
            _panelMode = true;
            GfxConfig.LogSource?.LogWarning($"[GFXConf] overlay window content failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Window content: all 18 §4.2 entries as live controls under their 5
    /// section headers, then the F10 hint + last sweep summary. No
    /// reflection — only the known <see cref="GfxConfig"/> entries are ever
    /// listed. A null entry (Bind failed) draws a placeholder instead of
    /// throwing. Used by both the GUILayout.Window and the panel path.
    /// </summary>
    private static void DrawContent()
    {
        if (_sectionStyle == null)
        {
            _sectionStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        }

        DrawSection("[PPv2]");
        DrawToggle(GfxConfig.DisableAmbientOcclusion);
        DrawToggle(GfxConfig.DisableChromaticAberration);
        DrawToggle(GfxConfig.DisableDepthOfField);
        DrawToggle(GfxConfig.DisableScreenSpaceReflections);
        DrawToggle(GfxConfig.DisableMotionBlur);
        DrawToggle(GfxConfig.DisableBloom);

        DrawSection("[SCPE]");
        DrawToggle(GfxConfig.DisableFog);
        DrawToggle(GfxConfig.DisableCloudShadows);
        DrawToggle(GfxConfig.DisableAmbientOcclusion2D);
        DrawToggle(GfxConfig.DisableBlur);
        DrawToggle(GfxConfig.DisableSharpen);

        DrawSection("[Volumetrics]");
        DrawToggle(GfxConfig.DisableVolumetricFog);
        DrawToggle(GfxConfig.DisablePlanarReflections);
        DrawToggle(GfxConfig.DisableAura2);

        DrawSection("[Antialiasing]");
        DrawOverrideMode();

        DrawSection("[General]");
        DrawToggle(GfxConfig.ReapplyOnSceneLoad);
        DrawDelaySeconds();
        DrawToggle(GfxConfig.EnableF10Overlay);

        GUILayout.Space(6f);
        GUILayout.Label("F10 = close");
        var summary = Sweeper.LastSummary;
        GUILayout.Label(
            string.IsNullOrEmpty(summary) ? "last sweep: (none yet)" : summary,
            _wrapStyle);
    }

    private static void DrawSection(string title)
    {
        GUILayout.Space(4f);
        GUILayout.Label(title, _sectionStyle);
    }

    /// <summary>
    /// One row per bool entry: checkbox state IS the current value, and the
    /// label repeats it so it is readable at a glance. A change writes the
    /// entry, saves the cfg and sweeps immediately.
    /// </summary>
    private static void DrawToggle(ConfigEntry<bool> entry)
    {
        if (entry == null)
        {
            GUILayout.Label("(config unavailable — see log)");
            return;
        }

        var current = entry.Value;
        var next = GUILayout.Toggle(current, $"{entry.Definition.Key} = {current}");
        if (next != current)
        {
            entry.Value = next;
            ApplyChange();
        }
    }

    /// <summary>
    /// OverrideMode is a string domain, not a bool → a button that cycles
    /// KeepOriginal → None → FastFXAA → FXAA → SMAA → TAA → wrap and shows
    /// the current value.
    /// </summary>
    private static void DrawOverrideMode()
    {
        var entry = GfxConfig.OverrideMode;
        if (entry == null)
        {
            GUILayout.Label("(config unavailable — see log)");
            return;
        }

        if (GUILayout.Button($"OverrideMode = {entry.Value}"))
        {
            entry.Value = NextOverrideMode(entry.Value);
            ApplyChange();
        }
    }

    /// <summary>
    /// Next value in the cycle. Values written are the spec §4.2 aliases the
    /// sweep parser understands; a full enum member name typed by hand into
    /// the cfg maps to its alias so the cycle keeps working, and an
    /// unrecognised value counts as KeepOriginal (next press → None).
    /// </summary>
    private static string NextOverrideMode(string current)
    {
        var index = -1;
        for (var i = 0; i < OverrideCycle.Length; i++)
        {
            if (string.Equals(OverrideCycle[i], current, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            switch (current?.Trim().ToLowerInvariant())
            {
                case "fastapproximateantialiasing":
                    index = 2; // FastFXAA
                    break;
                case "subpixelmorphologicalantialiasing":
                    index = 4; // SMAA
                    break;
                case "temporalantialiasing":
                    index = 5; // TAA
                    break;
                default:
                    index = 0; // unknown → treated as KeepOriginal, next = None
                    break;
            }
        }

        return OverrideCycle[(index + 1) % OverrideCycle.Length];
    }

    /// <summary>
    /// DelaySeconds: −/+ int adjustment clamped to 0..60, current value
    /// shown. At a clamp limit the buttons are a no-op (no write, no sweep).
    /// </summary>
    private static void DrawDelaySeconds()
    {
        var entry = GfxConfig.DelaySeconds;
        if (entry == null)
        {
            GUILayout.Label("(config unavailable — see log)");
            return;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label($"DelaySeconds = {entry.Value}", GUILayout.Width(170f));
        if (GUILayout.Button("-", GUILayout.Width(30f)))
        {
            SetDelay(entry, entry.Value - 1);
        }

        if (GUILayout.Button("+", GUILayout.Width(30f)))
        {
            SetDelay(entry, entry.Value + 1);
        }

        GUILayout.EndHorizontal();
    }

    private static void SetDelay(ConfigEntry<int> entry, int proposed)
    {
        var clamped = Math.Clamp(proposed, 0, 60);
        if (clamped == entry.Value)
        {
            return;
        }

        entry.Value = clamped;
        ApplyChange();
    }

    /// <summary>
    /// Contract: write entry → save cfg (source of truth) → immediate sweep
    /// labelled "overlay" (logs <c>scene=overlay sweep in 0s</c>, runs on
    /// the next pump tick).
    /// </summary>
    private static void ApplyChange()
    {
        GfxConfig.Save();
        Sweeper.Request("overlay", 0f);
    }

    private void OnDestroy()
    {
        // Self-heal: the game destroys pump objects created during the
        // boot-time scene transition (verified in testing — the object sat in
        // the DontDestroyOnLoad scene and was still torn down). Clearing the
        // flag lets the next sceneLoaded → EnsureCreated() rebuild the pump.
        _created = false;
    }
}

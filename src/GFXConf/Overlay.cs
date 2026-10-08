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
    /// Default panel geometry for the fallback path (size is fixed per
    /// contract; the x/y are only the DEFAULT position — the panel can be
    /// dragged by its title bar and <see cref="OpenOverlay"/> snaps it back
    /// here on every open). Per draw it is clamped into the screen by
    /// <see cref="ClampToScreen"/> so the panel never runs off-screen.
    /// </summary>
    private static readonly Rect PanelRect = new(16f, 16f, 360f, 620f);

    /// <summary>Title strip height — drawn on top of the panel background.</summary>
    private const float HeaderHeight = 26f;

    /// <summary>
    /// Session-only panel position, seeded from the <see cref="PanelRect"/>
    /// default (top-left). Never persisted to the cfg — <see cref="OpenOverlay"/>
    /// snaps it back to the default on every open.
    /// </summary>
    private static Vector2 _panelPos = new(PanelRect.x, PanelRect.y);

    /// <summary>True while a title-bar drag is in progress.</summary>
    private static bool _dragging;

    /// <summary>Mouse offset from the panel origin at drag start.</summary>
    private static Vector2 _dragOffset;

    /// <summary>
    /// Scroll position of the controls area. One field shared by both draw
    /// paths (only one path renders per session — <see cref="_panelMode"/>
    /// is sticky), so switching paths never loses the user's position.
    /// </summary>
    private static Vector2 _scroll;

    /// <summary>Bold section header style, created once on first draw.</summary>
    private static GUIStyle _sectionStyle;

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
                    CloseOverlay();
                }
                else
                {
                    var gate = GfxConfig.EnableF10Overlay;
                    if (gate != null && gate.Value)
                    {
                        OpenOverlay();
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
            CloseOverlay(); // hides first, then restores game input
            GfxConfig.LogSource?.LogWarning($"[GFXConf] overlay draw failed: {ex}");
        }
    }

    /// <summary>
    /// Fixed-panel fallback: opaque background box, the controls in a
    /// scroll view (all 18 entries reachable at any resolution) filling the
    /// panel below the header, then the title strip with the close X.
    /// Begin/EndArea and Begin/EndScrollView
    /// are protected by finally so a throwing control can never leave the
    /// global layout stack unbalanced (it would corrupt every later
    /// GUILayout draw, including the game's own).
    /// </summary>
    private static void DrawPanel()
    {
        HandleHeaderDrag();
        var panel = ClampToScreen(new Rect(
            _panelPos.x, _panelPos.y, PanelRect.width, PanelRect.height));
        var header = new Rect(panel.x, panel.y, panel.width, HeaderHeight);
        var scroll = new Rect(
            panel.x + 2f, header.yMax + 2f, panel.width - 4f,
            Mathf.Max(40f, panel.yMax - header.yMax - 4f));

        FillOpaque(panel); // R2 round 4: solid, fully opaque dark background
        GUI.Box(panel, string.Empty, GUI.skin.window);

        GUILayout.BeginArea(scroll);
        var next = _scroll;
        try
        {
            next = GUILayout.BeginScrollView(_scroll);
            try
            {
                DrawControls();
            }
            finally
            {
                GUILayout.EndScrollView();
            }
        }
        finally
        {
            GUILayout.EndArea();
        }

        _scroll = next;

        GUI.Box(header, "GFXConf overlay", GUI.skin.box);
        DrawCloseButton(header);
    }

    /// <summary>
    /// Title-bar dragging (session-only): MouseDown inside the header
    /// (outside the close-X rect, so clicking X still closes instead of
    /// starting a drag) latches a grab with the grab offset; MouseDrag
    /// moves <see cref="_panelPos"/>, clamped to the screen every event so
    /// the panel can never be dragged off-screen; left-button MouseUp
    /// releases. Runs
    /// only on IMGUI mouse events — no per-frame allocations. Called at the
    /// top of <see cref="DrawPanel"/> before the geometry is computed, so
    /// the drag and the drawn panel always agree on the position.
    /// </summary>
    private static void HandleHeaderDrag()
    {
        var e = Event.current;
        if (e == null) return;

        var panel = ClampToScreen(new Rect(
            _panelPos.x, _panelPos.y, PanelRect.width, PanelRect.height));
        var header = new Rect(panel.x, panel.y, panel.width, HeaderHeight);
        var close = new Rect(header.xMax - 24f, header.y + 4f, 20f, HeaderHeight - 8f);
        var mouse = e.mousePosition;

        if (e.type == EventType.MouseDown && e.button == 0
            && header.Contains(mouse) && !close.Contains(mouse))
        {
            _dragging = true;
            _dragOffset = mouse - new Vector2(panel.x, panel.y);
            e.Use();
        }
        else if (e.type == EventType.MouseDrag && _dragging)
        {
            var clamped = ClampToScreen(new Rect(
                mouse.x - _dragOffset.x, mouse.y - _dragOffset.y,
                PanelRect.width, PanelRect.height));
            _panelPos = new Vector2(clamped.x, clamped.y);
            e.Use();
        }
        else if (e.type == EventType.MouseUp && e.button == 0)
        {
            _dragging = false;
        }
    }

    /// <summary>
    /// Fills <paramref name="rect"/> with a solid opaque dark colour
    /// (alpha 1) via the white texture tinted by <c>GUI.color</c> — the
    /// skin-independent way to guarantee a fully opaque panel background.
    /// <c>GUI.color</c> is always restored, even on a throw.
    /// </summary>
    private static void FillOpaque(Rect rect)
    {
        var previous = GUI.color;
        try
        {
            GUI.color = new Color(0.09f, 0.10f, 0.12f, 1f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
        }
        finally
        {
            GUI.color = previous;
        }
    }

    /// <summary>
    /// Shrinks/shifts the nominal panel rect so it fits inside
    /// <c>Screen.width/Height</c> (small resolutions never cut the panel
    /// off); recomputed every draw so a resolution change applies live.
    /// </summary>
    private static Rect ClampToScreen(Rect rect)
    {
        rect.width = Mathf.Min(rect.width, Mathf.Max(80f, Screen.width - 8f));
        rect.height = Mathf.Min(rect.height, Mathf.Max(80f, Screen.height - 8f));
        rect.x = Mathf.Clamp(rect.x, 0f, Mathf.Max(0f, Screen.width - rect.width));
        rect.y = Mathf.Clamp(rect.y, 0f, Mathf.Max(0f, Screen.height - rect.height));
        return rect;
    }

    /// <summary>
    /// Small close button at the top-right of the title row. Behaves
    /// exactly like F10 close (same <see cref="CloseOverlay"/>): the
    /// ruling T6 #1 gate covers OPENING only, and the X is only visible
    /// while the overlay is already open, so no gate interaction exists.
    /// </summary>
    private static void DrawCloseButton(Rect titleRow)
    {
        var closeRect = new Rect(titleRow.xMax - 24f, titleRow.y + 4f, 20f, HeaderHeight - 8f);
        if (GUI.Button(closeRect, "X"))
        {
            CloseOverlay();
        }
    }

    /// <summary>
    /// The single open action (F10): snap the panel back to its default
    /// top-left position, show the overlay, and log one line. Log string is
    /// byte-identical to the pinned
    /// <c>[GFXConf] overlay opened</c> format.
    /// </summary>
    private static void OpenOverlay()
    {
        _panelPos = new Vector2(PanelRect.x, PanelRect.y); // snap back to default
        _dragging = false;
        _windowRect = new Rect(20f, 20f, 360f, 520f);
        _visible = true;
        GfxConfig.LogSource?.LogInfo("[GFXConf] overlay opened");
    }

    /// <summary>
    /// The single close action (F10, the X button and the OnGUI failure
    /// path): hide the overlay and log one line. Log string is byte-identical
    /// to the pinned <c>[GFXConf] overlay closed</c> format. No gate check —
    /// closing an open overlay is always allowed (ruling T6 #1). Every
    /// visibility transition goes through <see cref="OpenOverlay"/> and
    /// <see cref="CloseOverlay"/>.
    /// </summary>
    private static void CloseOverlay()
    {
        _visible = false;
        _dragging = false;
        GfxConfig.LogSource?.LogInfo("[GFXConf] overlay closed");
    }

    /// <summary>
    /// <c>GUILayout.Window</c> callback. In the IL2CPP trampoline a thrown
    /// exception never reaches <c>OnGUI</c>'s catch (it is swallowed one
    /// frame deeper and logged by Il2CppInterop as an error), so the content
    /// guards itself: a failure warns once, switches to the panel path and
    /// draws nothing for this event. Shares the panel path's presentation:
    /// opaque background, close X, scrollable controls.
    /// </summary>
    private static void DrawWindow(int id)
    {
        try
        {
            FillOpaque(new Rect(0f, 0f, _windowRect.width, _windowRect.height));
            var titleRow = new Rect(0f, 0f, _windowRect.width, HeaderHeight);
            GUI.Label(new Rect(6f, 4f, _windowRect.width - 30f, HeaderHeight - 8f),
                "GFXConf overlay");
            DrawCloseButton(titleRow);

            var next = GUILayout.BeginScrollView(_scroll);
            try
            {
                DrawControls();
            }
            finally
            {
                GUILayout.EndScrollView();
            }

            _scroll = next;
        }
        catch (Exception ex)
        {
            _panelMode = true;
            GfxConfig.LogSource?.LogWarning($"[GFXConf] overlay window content failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The scrollable controls: all 18 §4.2 entries as live controls under
    /// their 5 section headers. No reflection — only the known
    /// <see cref="GfxConfig"/> entries are ever listed. A null entry (Bind
    /// failed) draws a placeholder instead of throwing. Used by both the
    /// GUILayout.Window and the panel path.
    /// </summary>
    private static void DrawControls()
    {
        if (_sectionStyle == null)
        {
            _sectionStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
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
    }

    private static void DrawSection(string title)
    {
        GUILayout.Space(4f);
        GUILayout.Label(title, _sectionStyle);
    }

    /// <summary>
    /// One row per bool entry: checkbox state IS the current value; the
    /// label is the bare config key (value shown by the checkbox itself).
    /// A change writes the entry, saves the cfg and sweeps immediately.
    /// </summary>
    private static void DrawToggle(ConfigEntry<bool> entry)
    {
        if (entry == null)
        {
            GUILayout.Label("(config unavailable — see log)");
            return;
        }

        var current = entry.Value;
        var next = GUILayout.Toggle(current, entry.Definition.Key);
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
        // Teardown while open should log the normal close transition before
        // the next sceneLoaded → EnsureCreated. Guarded: a failure must never
        // escape into the Unity teardown callback.
        try
        {
            if (_visible)
            {
                CloseOverlay();
            }
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] teardown close failed: {ex}");
        }

        // Self-heal: the game destroys pump objects created during the
        // boot-time scene transition (verified in testing — the object sat in
        // the DontDestroyOnLoad scene and was still torn down). Clearing the
        // flag lets the next sceneLoaded → EnsureCreated() rebuild the pump.
        _created = false;
    }
}

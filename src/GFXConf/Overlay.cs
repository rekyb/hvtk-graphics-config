using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GFXConf;

/// <summary>
/// Hidden IL2CPP-injected pump component on a DontDestroyOnLoad GameObject.
/// Owns the plugin's per-frame work: <c>Update()</c> ticks the sweep
/// scheduler (spec §4.1) and toggles the F10 overlay; <c>OnGUI()</c> draws
/// the overlay window — one control per overlay-editable §4.2 entry, grouped by
/// section. Scene settings remain config-file-only. The GUI only reads/writes
/// <see cref="GfxConfig"/> entries: on any change it saves the cfg and schedules
/// an immediate sweep (spec §4.1 step 4). Drawing costs nothing while closed.
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

    /// <summary>Overlay visibility — F10 toggles it (open when closed, close when open).</summary>
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

    // Scene suppression is event-driven; Update waits for DelaySeconds, then
    // completes the one-rendered-frame capture and polls the user capture key.
    private static bool _sceneTrackingAvailable;
    private static bool _sceneSuppressionBroken;
    private static bool _sceneSuppressionActive;
    private static float _nextSuppressionReconcileTime;
    private static bool _snapshotDelayPending;
    private static float _snapshotReadyTime;
    private static bool _snapshotCapturePending;
    private static int _snapshotRenderFramesRemaining;
    // Temporary QA check: verify the game preserves camera masks after activation.
    private static bool _cameraMaskCheckPending;
    private static float _cameraMaskCheckReadyTime;
    private static bool _snapshotOverlayVisibilitySaved;
    private static bool _snapshotOverlayWasVisible;
    private static bool _captureHotkeyBroken;
    private static readonly Dictionary<int, string> _loadedSceneNames = new();
    private static readonly List<int> _sceneLoadOrder = new();
    private static string _lastLoadedSceneName;
    private static readonly HashSet<string> _activeAllowedScenes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Canvas> _captureCanvases = new();
    private static readonly List<bool> _captureCanvasStates = new();
    private static readonly List<Camera> _suppressedCameras = new();
    private static readonly List<int> _originalCameraMasks = new();
    private static readonly List<PostProcessLayer> _suppressedPostProcessLayers = new();
    private static readonly List<bool> _originalPostProcessStates = new();
    private static GameObject _snapshotCanvas;
    private static Texture2D _snapshotTexture;

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
    /// Cycle options for a string-domain override: <c>Value</c> is written to
    /// the cfg (the spec §4.2 domain the sweep parser understands), <c>Label</c>
    /// is the human-readable text the cycle button shows, so a numeric value
    /// like "0.6" reads as "Fastest".
    /// </summary>
    private static readonly (string Value, string Label)[] OverrideModeOptions =
    {
        ("KeepOriginal", "Keep Original"), ("None", "None"),
        ("FastFXAA", "Fast FXAA"), ("FXAA", "FXAA"),
        ("SMAA", "SMAA"), ("TAA", "TAA")
    };

    private static readonly (string Value, string Label)[] ShadowDistanceOptions =
    {
        ("KeepOriginal", "Keep Original"), ("40", "Quality"),
        ("30", "Balanced"), ("25", "Fastest")
    };

    private static readonly (string Value, string Label)[] ShadowResolutionOptions =
    {
        ("KeepOriginal", "Keep Original"), ("Low", "Low"),
        ("Medium", "Medium"), ("High", "High"),
        ("VeryHigh", "Very High")
    };

    private static readonly (string Value, string Label)[] LodBiasOptions =
    {
        ("KeepOriginal", "Keep Original"), ("0.8", "Quality"),
        ("0.7", "Balanced"), ("0.6", "Fastest")
    };

    private static readonly (string Value, string Label)[] MsaaOptions =
    {
        ("KeepOriginal", "Keep Original"), ("0", "Off"),
        ("2", "2x"), ("4", "4x"), ("8", "8x")
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

        if (_snapshotDelayPending)
        {
            try
            {
                if (Time.realtimeSinceStartup >= _snapshotReadyTime)
                {
                    _snapshotDelayPending = false;
                    BeginSnapshotCapture();
                }
            }
            catch (Exception ex)
            {
                DisableSceneSuppression($"snapshot delay failed: {ex.Message}");
            }
        }

        if (_snapshotCapturePending)
        {
            if (_snapshotRenderFramesRemaining > 0)
            {
                _snapshotRenderFramesRemaining--;
            }
            else
            {
                CompleteSnapshotCapture();
            }
        }

        if (_cameraMaskCheckPending)
        {
            try
            {
                if (Time.realtimeSinceStartup >= _cameraMaskCheckReadyTime)
                {
                    _cameraMaskCheckPending = false;
                    LogCurrentCameraMasks();
                }
            }
            catch (Exception ex)
            {
                _cameraMaskCheckPending = false;
                GfxConfig.LogSource?.LogWarning($"[GFXConf] camera mask audit failed: {ex.Message}");
            }
        }

        if (_sceneSuppressionActive)
        {
            try
            {
                var now = Time.realtimeSinceStartup;
                if (now >= _nextSuppressionReconcileTime)
                {
                    _nextSuppressionReconcileTime = now + 1f;
                    ReconcileSceneSuppression();
                }
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] scene suppression reconciliation failed: {ex.Message}");
            }
        }

        HandleSceneCaptureHotkey();

        // F10 toggles the overlay: open when closed, close when open. GetKeyDown
        // is only read once per frame, and any thrown failure latches F10 off
        // for the session so a persistent input error cannot warn every frame.
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
                    OpenOverlay();
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
    /// Runs after every <c>Update</c>: re-force the held effects off, so a
    /// profile the game re-applied during its Update is off before this frame
    /// renders. See <see cref="Sweeper.Reapply"/>.
    /// </summary>
    private void LateUpdate()
    {
        try
        {
            Sweeper.Reapply();
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] effect hold failed: {ex}");
        }
    }

    private static void HandleSceneCaptureHotkey()
    {
        if (_captureHotkeyBroken)
        {
            return;
        }

        try
        {
            var hotkey = GfxConfig.CaptureSceneHotkey;
            if (hotkey != null && hotkey.Value != KeyCode.None
                && UnityEngine.Input.GetKeyDown(hotkey.Value))
            {
                CaptureLatestSceneToAllowlist();
            }
        }
        catch (Exception ex)
        {
            _captureHotkeyBroken = true;
            GfxConfig.LogSource?.LogWarning($"[GFXConf] scene capture hotkey disabled: {ex}");
        }
    }

    private static void CaptureLatestSceneToAllowlist()
    {
        try
        {
            if (!_sceneTrackingAvailable || _sceneSuppressionBroken)
            {
                GfxConfig.LogSource?.LogInfo("[GFXConf] scene capture skipped: scene tracking unavailable");
                return;
            }

            if (GfxConfig.EnableSceneSuppression?.Value != true)
            {
                GfxConfig.LogSource?.LogInfo("[GFXConf] scene capture skipped: scene pause disabled");
                return;
            }

            if (string.IsNullOrWhiteSpace(_lastLoadedSceneName))
            {
                GfxConfig.LogSource?.LogInfo("[GFXConf] scene capture skipped: no tracked loaded scene");
                return;
            }

            var entry = GfxConfig.SceneSuppressionAllowlist;
            if (entry == null)
            {
                GfxConfig.LogSource?.LogWarning("[GFXConf] scene capture skipped: allowlist config unavailable");
                return;
            }

            var scenes = GfxConfig.ParseSceneSuppressionAllowlist(entry.Value);
            var sceneName = _lastLoadedSceneName.Trim();
            foreach (var listed in scenes)
            {
                if (string.Equals(listed, sceneName, StringComparison.OrdinalIgnoreCase))
                {
                    GfxConfig.LogSource?.LogInfo($"[GFXConf] scene capture skipped: already allowed '{sceneName}'");
                    return;
                }
            }

            var previousValue = entry.Value;
            scenes.Add(sceneName);
            entry.Value = string.Join(", ", scenes);
            if (!GfxConfig.Save())
            {
                entry.Value = previousValue;
                GfxConfig.LogSource?.LogWarning($"[GFXConf] scene capture not saved: '{sceneName}'");
                return;
            }

            GfxConfig.LogSource?.LogInfo($"[GFXConf] scene added to allowlist: '{sceneName}'");
            EvaluateSceneSuppression();
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] scene capture failed: {ex}");
        }
    }

    internal static void InitializeSceneSuppression(bool trackingAvailable)
    {
        try
        {
            _sceneTrackingAvailable = trackingAvailable;
            if (!trackingAvailable)
            {
                DisableSceneSuppression("scene event subscription unavailable");
                return;
            }

            _loadedSceneNames.Clear();
            _sceneLoadOrder.Clear();
            _lastLoadedSceneName = null;
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                {
                    TrackLoadedScene(scene);
                }
            }

            EvaluateSceneSuppression();
        }
        catch (Exception ex)
        {
            DisableSceneSuppression($"initial scene scan failed: {ex.Message}");
        }
    }

    internal static void OnSceneLoaded(Scene scene)
    {
        try
        {
            if (!_sceneTrackingAvailable || _sceneSuppressionBroken)
            {
                return;
            }

            TrackLoadedScene(scene);
            EvaluateSceneSuppression();
        }
        catch (Exception ex)
        {
            DisableSceneSuppression($"sceneLoaded tracking failed: {ex.Message}");
        }
    }

    internal static void OnSceneUnloaded(Scene scene)
    {
        try
        {
            if (!_sceneTrackingAvailable || _sceneSuppressionBroken)
            {
                return;
            }

            var handle = scene.handle;
            _loadedSceneNames.Remove(handle);
            _sceneLoadOrder.Remove(handle);
            RefreshLastLoadedSceneName();
            EvaluateSceneSuppression();
        }
        catch (Exception ex)
        {
            DisableSceneSuppression($"sceneUnloaded tracking failed: {ex.Message}");
        }
    }

    private static void TrackLoadedScene(Scene scene)
    {
        var handle = scene.handle;
        _loadedSceneNames[handle] = scene.name;
        _sceneLoadOrder.Remove(handle);
        _sceneLoadOrder.Add(handle);
        _lastLoadedSceneName = scene.name;
    }

    private static void RefreshLastLoadedSceneName()
    {
        _lastLoadedSceneName = null;
        for (var i = _sceneLoadOrder.Count - 1; i >= 0; i--)
        {
            if (_loadedSceneNames.TryGetValue(_sceneLoadOrder[i], out var sceneName))
            {
                _lastLoadedSceneName = sceneName;
                return;
            }
        }
    }

    private static void EvaluateSceneSuppression()
    {
        try
        {
            // Master switch (v0.4.0): while off, never suppress and always
            // restore any active suppression (silent no-op when none active).
            if (GfxConfig.EnableSceneSuppression?.Value != true)
            {
                RestoreSceneSuppression("scene pause disabled");
                return;
            }

            if (!_sceneTrackingAvailable || _sceneSuppressionBroken)
            {
                return;
            }

            var allowlist = GfxConfig.SceneSuppressionAllowlist?.Value;
            if (!GfxConfig.IsSceneSuppressionEligible(_loadedSceneNames.Values, allowlist))
            {
                RestoreSceneSuppression("no allowlisted scene loaded");
                return;
            }

            var allowlistedScenes = new HashSet<string>(
                GfxConfig.ParseSceneSuppressionAllowlist(allowlist),
                StringComparer.OrdinalIgnoreCase);
            var currentTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sceneName in _loadedSceneNames.Values)
            {
                if (allowlistedScenes.Contains(sceneName))
                {
                    currentTargets.Add(sceneName);
                }
            }

            if ((_sceneSuppressionActive || _snapshotDelayPending || _snapshotCapturePending)
                && _activeAllowedScenes.SetEquals(currentTargets))
            {
                return;
            }

            if (_sceneSuppressionActive || _snapshotDelayPending || _snapshotCapturePending || _snapshotCanvas != null)
            {
                RestoreSceneSuppression("allowlisted scene set changed");
            }

            _activeAllowedScenes.Clear();
            _activeAllowedScenes.UnionWith(currentTargets);
            _snapshotReadyTime = Time.realtimeSinceStartup + GfxConfig.DelaySeconds;
            _snapshotDelayPending = true;
        }
        catch (Exception ex)
        {
            DisableSceneSuppression($"eligibility evaluation failed: {ex.Message}");
        }
    }

    private static void BeginSnapshotCapture()
    {
        try
        {
            RestoreCaptureCanvases();
            DestroySnapshotDisplay();
            _snapshotOverlayWasVisible = _visible;
            _snapshotOverlayVisibilitySaved = true;
            _visible = false;

            var found = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(Canvas)));
            if (found != null)
            {
                foreach (var obj in found)
                {
                    if (obj == null)
                    {
                        continue;
                    }

                    var canvas = obj as Canvas
                        ?? (Activator.CreateInstance(typeof(Canvas), obj.Pointer) as Canvas);
                    if (canvas == null || canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    {
                        continue;
                    }

                    _captureCanvases.Add(canvas);
                    _captureCanvasStates.Add(canvas.enabled);
                    if (canvas.enabled)
                    {
                        canvas.enabled = false;
                    }
                }
            }

            _snapshotRenderFramesRemaining = 1;
            _snapshotCapturePending = true;
        }
        catch (Exception ex)
        {
            _snapshotCapturePending = false;
            _snapshotRenderFramesRemaining = 0;
            RestoreCaptureCanvases();
            DestroySnapshotDisplay();
            _activeAllowedScenes.Clear();
            GfxConfig.LogSource?.LogWarning($"[GFXConf] scene snapshot setup failed; rendering remains active: {ex}");
        }
    }

    private static void CompleteSnapshotCapture()
    {
        _snapshotCapturePending = false;
        _snapshotRenderFramesRemaining = 0;
        try
        {
            _snapshotTexture = ScreenCapture.CaptureScreenshotAsTexture();
            if (_snapshotTexture == null)
            {
                throw new InvalidOperationException("CaptureScreenshotAsTexture returned null");
            }

            var width = _snapshotTexture.width;
            var height = _snapshotTexture.height;
            CreateSnapshotDisplay();
            ApplySceneSuppression();
            _sceneSuppressionActive = true;
            _nextSuppressionReconcileTime = Time.realtimeSinceStartup + 1f;
            _cameraMaskCheckReadyTime = Time.realtimeSinceStartup + 1f;
            _cameraMaskCheckPending = true;
            GfxConfig.LogSource?.LogInfo(
                $"[GFXConf] scene suppression active: targets={string.Join(",", _activeAllowedScenes)} " +
                $"snapshot={width}x{height} cameras={_suppressedCameras.Count} " +
                $"ppLayers={_suppressedPostProcessLayers.Count}");
        }
        catch (Exception ex)
        {
            RestoreRenderState();
            DestroySnapshotDisplay();
            _sceneSuppressionActive = false;
            _activeAllowedScenes.Clear();
            GfxConfig.LogSource?.LogWarning($"[GFXConf] scene snapshot failed; rendering remains active: {ex}");
        }
        finally
        {
            RestoreCaptureCanvases();
        }
    }

    private static void LogCurrentCameraMasks()
    {
        try
        {
            var cameras = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(Camera)));
            if (cameras == null)
            {
                throw new InvalidOperationException("Camera query returned null");
            }

            var total = 0;
            var zeroMask = 0;
            foreach (var obj in cameras)
            {
                if (obj == null)
                {
                    continue;
                }

                var camera = obj as Camera
                    ?? (Activator.CreateInstance(typeof(Camera), obj.Pointer) as Camera);
                if (camera == null)
                {
                    continue;
                }

                total++;
                if (camera.cullingMask == 0)
                {
                    zeroMask++;
                }
            }

            GfxConfig.LogSource?.LogInfo(
                $"[GFXConf] camera mask audit: tracked={_suppressedCameras.Count} " +
                $"current={total} zero={zeroMask} nonzero={total - zeroMask}");
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] camera mask audit failed: {ex.Message}");
        }
    }

    private static void CreateSnapshotDisplay()
    {
        var minimumSortingOrder = 0;
        foreach (var canvas in _captureCanvases)
        {
            if (canvas != null)
            {
                minimumSortingOrder = Math.Min(minimumSortingOrder, canvas.sortingOrder);
            }
        }

        var go = new GameObject("GFXConfFrozenFrame");
        _snapshotCanvas = go;
        var rect = go.AddComponent<RectTransform>();
        var canvasDisplay = go.AddComponent<Canvas>();
        var image = go.AddComponent<RawImage>();
        canvasDisplay.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasDisplay.overrideSorting = true;
        canvasDisplay.sortingOrder = minimumSortingOrder > int.MinValue
            ? minimumSortingOrder - 1
            : int.MinValue;

        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        image.texture = _snapshotTexture;
        image.raycastTarget = false;
    }

    private static void ApplySceneSuppression()
    {
        var cameras = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.From(typeof(Camera)));
        if (cameras == null)
        {
            throw new InvalidOperationException("Camera query returned null");
        }

        var cameraFailure = false;
        foreach (var obj in cameras)
        {
            try
            {
                if (obj == null)
                {
                    continue;
                }

                var camera = obj as Camera
                    ?? (Activator.CreateInstance(typeof(Camera), obj.Pointer) as Camera);
                if (camera == null)
                {
                    continue;
                }

                var originalMask = camera.cullingMask;
                _suppressedCameras.Add(camera);
                _originalCameraMasks.Add(originalMask);
                camera.cullingMask = 0;
            }
            catch (Exception ex)
            {
                cameraFailure = true;
                GfxConfig.LogSource?.LogWarning($"[GFXConf] camera suppression failed: {ex.Message}");
            }
        }

        if (_suppressedCameras.Count == 0)
        {
            throw new InvalidOperationException("No cameras were available for scene suppression");
        }

        if (cameraFailure)
        {
            throw new InvalidOperationException("Camera suppression incomplete; rendering restored");
        }

        var layers = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.From(typeof(PostProcessLayer)));
        if (layers != null)
        {
            var layerFailure = false;
            foreach (var obj in layers)
            {
                try
                {
                    if (obj == null)
                    {
                        continue;
                    }

                    var layer = obj as PostProcessLayer
                        ?? (Activator.CreateInstance(typeof(PostProcessLayer), obj.Pointer) as PostProcessLayer);
                    if (layer == null)
                    {
                        continue;
                    }

                    var originalEnabled = layer.enabled;
                    _suppressedPostProcessLayers.Add(layer);
                    _originalPostProcessStates.Add(originalEnabled);
                    layer.enabled = false;
                }
                catch (Exception ex)
                {
                    layerFailure = true;
                    GfxConfig.LogSource?.LogWarning($"[GFXConf] post-process suppression failed: {ex.Message}");
                }
            }

            if (layerFailure)
            {
                throw new InvalidOperationException("Post-process suppression incomplete; rendering restored");
            }
        }
    }

    private static void ReconcileSceneSuppression()
    {
        try
        {
            var cameras = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(Camera)));
            if (cameras == null)
            {
                throw new InvalidOperationException("Camera query returned null");
            }

            var addedCameras = 0;
            var resetCameras = 0;
            foreach (var obj in cameras)
            {
                try
                {
                    if (obj == null)
                    {
                        continue;
                    }

                    var camera = obj as Camera
                        ?? (Activator.CreateInstance(typeof(Camera), obj.Pointer) as Camera);
                    if (camera == null)
                    {
                        continue;
                    }

                    var tracked = IsCameraTracked(camera);
                    var currentMask = camera.cullingMask;
                    if (!tracked)
                    {
                        _suppressedCameras.Add(camera);
                        _originalCameraMasks.Add(currentMask);
                        addedCameras++;
                    }

                    if (currentMask != 0)
                    {
                        camera.cullingMask = 0;
                        if (tracked)
                        {
                            resetCameras++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    GfxConfig.LogSource?.LogWarning($"[GFXConf] camera reconciliation failed: {ex.Message}");
                }
            }

            var layers = UnityEngine.Object.FindObjectsOfType(
                Il2CppInterop.Runtime.Il2CppType.From(typeof(PostProcessLayer)));
            var addedLayers = 0;
            var reenabledLayers = 0;
            if (layers != null)
            {
                foreach (var obj in layers)
                {
                    try
                    {
                        if (obj == null)
                        {
                            continue;
                        }

                        var layer = obj as PostProcessLayer
                            ?? (Activator.CreateInstance(typeof(PostProcessLayer), obj.Pointer) as PostProcessLayer);
                        if (layer == null)
                        {
                            continue;
                        }

                        var tracked = IsPostProcessLayerTracked(layer);
                        var wasEnabled = layer.enabled;
                        if (!tracked)
                        {
                            _suppressedPostProcessLayers.Add(layer);
                            _originalPostProcessStates.Add(wasEnabled);
                            addedLayers++;
                        }

                        if (wasEnabled)
                        {
                            layer.enabled = false;
                            if (tracked)
                            {
                                reenabledLayers++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        GfxConfig.LogSource?.LogWarning($"[GFXConf] post-process reconciliation failed: {ex.Message}");
                    }
                }
            }

            if (addedCameras > 0 || resetCameras > 0 || addedLayers > 0 || reenabledLayers > 0)
            {
                GfxConfig.LogSource?.LogInfo(
                    $"[GFXConf] scene suppression reconciled: newCameras={addedCameras} " +
                    $"resetCameras={resetCameras} newPpLayers={addedLayers} reenabledPpLayers={reenabledLayers}");
            }
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] scene suppression reconciliation failed: {ex.Message}");
        }
    }

    private static bool IsCameraTracked(Camera candidate)
    {
        foreach (var tracked in _suppressedCameras)
        {
            if (tracked != null && tracked.Pointer == candidate.Pointer)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPostProcessLayerTracked(PostProcessLayer candidate)
    {
        foreach (var tracked in _suppressedPostProcessLayers)
        {
            if (tracked != null && tracked.Pointer == candidate.Pointer)
            {
                return true;
            }
        }

        return false;
    }

    private static void RestoreSceneSuppression(string reason)
    {
        var hadSuppression = _sceneSuppressionActive || _snapshotDelayPending || _snapshotCapturePending
            || _snapshotCanvas != null || _suppressedCameras.Count > 0;
        _cameraMaskCheckPending = false;
        _cameraMaskCheckReadyTime = 0f;
        _nextSuppressionReconcileTime = 0f;
        _snapshotDelayPending = false;
        _snapshotReadyTime = 0f;
        _snapshotCapturePending = false;
        _snapshotRenderFramesRemaining = 0;
        RestoreCaptureCanvases();
        RestoreRenderState();
        DestroySnapshotDisplay();
        _sceneSuppressionActive = false;
        _activeAllowedScenes.Clear();
        if (hadSuppression)
        {
            GfxConfig.LogSource?.LogInfo($"[GFXConf] scene suppression restored: reason={reason}");
        }
    }

    private static void RestoreRenderState()
    {
        for (var i = 0; i < _suppressedCameras.Count; i++)
        {
            try
            {
                var camera = _suppressedCameras[i];
                if (camera != null)
                {
                    camera.cullingMask = _originalCameraMasks[i];
                }
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] camera restore failed: {ex.Message}");
            }
        }

        for (var i = 0; i < _suppressedPostProcessLayers.Count; i++)
        {
            try
            {
                var layer = _suppressedPostProcessLayers[i];
                if (layer != null)
                {
                    layer.enabled = _originalPostProcessStates[i];
                }
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] post-process restore failed: {ex.Message}");
            }
        }

        _suppressedCameras.Clear();
        _originalCameraMasks.Clear();
        _suppressedPostProcessLayers.Clear();
        _originalPostProcessStates.Clear();
    }

    private static void RestoreCaptureCanvases()
    {
        for (var i = 0; i < _captureCanvases.Count; i++)
        {
            try
            {
                var canvas = _captureCanvases[i];
                if (canvas != null)
                {
                    canvas.enabled = _captureCanvasStates[i];
                }
            }
            catch (Exception ex)
            {
                GfxConfig.LogSource?.LogWarning($"[GFXConf] UI canvas restore failed: {ex.Message}");
            }
        }

        _captureCanvases.Clear();
        _captureCanvasStates.Clear();
        if (_snapshotOverlayVisibilitySaved)
        {
            _visible = _snapshotOverlayWasVisible;
            _snapshotOverlayVisibilitySaved = false;
        }
    }

    private static void DestroySnapshotDisplay()
    {
        try
        {
            if (_snapshotCanvas != null)
            {
                _snapshotCanvas.SetActive(false);
                UnityEngine.Object.Destroy(_snapshotCanvas);
            }
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] snapshot display cleanup failed: {ex.Message}");
        }
        finally
        {
            _snapshotCanvas = null;
        }

        try
        {
            if (_snapshotTexture != null)
            {
                UnityEngine.Object.Destroy(_snapshotTexture);
            }
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] snapshot texture cleanup failed: {ex.Message}");
        }
        finally
        {
            _snapshotTexture = null;
        }
    }

    private static void DisableSceneSuppression(string reason)
    {
        _sceneSuppressionBroken = true;
        _sceneTrackingAvailable = false;
        RestoreSceneSuppression("tracking unavailable");
        GfxConfig.LogSource?.LogWarning($"[GFXConf] scene suppression disabled for session: {reason}");
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
    /// Fixed-panel fallback: opaque background box, the 18 overlay-editable
    /// controls in a scroll view (all reachable at any resolution) filling the
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
    /// The scrollable controls: the overlay-editable §4.2 entries grouped under
    /// their section headers, ordered Quality, then the post-processing,
    /// volumetrics and extra sections. Scene settings remain config-file-only. No
    /// reflection — only the known
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

        DrawSection("[Quality]");
        DrawOptionCycle(GfxConfig.OverrideMode, OverrideModeOptions);
        DrawOptionCycle(GfxConfig.ShadowDistance, ShadowDistanceOptions);
        DrawOptionCycle(GfxConfig.ShadowResolution, ShadowResolutionOptions);
        DrawOptionCycle(GfxConfig.LodBias, LodBiasOptions);
        DrawOptionCycle(GfxConfig.MSAA, MsaaOptions);

        DrawSection("[Post Processing Stack v2]");
        DrawToggle(GfxConfig.DisableAmbientOcclusion);
        DrawToggle(GfxConfig.DisableChromaticAberration);
        DrawToggle(GfxConfig.DisableDepthOfField);
        DrawToggle(GfxConfig.DisableScreenSpaceReflections);
        DrawToggle(GfxConfig.DisableMotionBlur);
        DrawToggle(GfxConfig.DisableBloom);

        DrawSection("[Scene Color Processing Effects]");
        DrawToggle(GfxConfig.DisableFog);
        DrawToggle(GfxConfig.DisableCloudShadows);
        DrawToggle(GfxConfig.DisableAmbientOcclusion2D);
        DrawToggle(GfxConfig.DisableBlur);
        DrawToggle(GfxConfig.DisableSharpen);

        DrawSection("[Volumetrics]");
        DrawToggle(GfxConfig.DisableVolumetricFog);
        DrawToggle(GfxConfig.DisablePlanarReflections);
        DrawToggle(GfxConfig.DisableAura2);

        DrawSection("[Extra]");
        DrawSceneSuppressionToggle();
        var captureKey = GfxConfig.CaptureSceneHotkey?.Value ?? KeyCode.F9;
        GUILayout.Label($"Press {captureKey} in an idle scene to add it to the suppression list.");
    }

    private static void DrawSection(string title)
    {
        GUILayout.Space(4f);
        GUILayout.Label(title, _sectionStyle);
    }

    /// <summary>
    /// Human-readable copy for a config key. Config keys are PascalCase
    /// (mirroring spec §4.2); the overlay shows normal sentence-style labels
    /// instead of the raw identifier, phrased positively — a <c>Disable*</c>
    /// key reads "Enable &lt;effect&gt;" (see <see cref="DrawToggle"/>, which
    /// inverts that key's box). Unknown keys fall back to the raw key.
    /// </summary>
    private static string Label(string key)
    {
        switch (key)
        {
            case nameof(GfxConfig.DisableAmbientOcclusion): return "Enable Ambient Occlusion";
            case nameof(GfxConfig.DisableChromaticAberration): return "Enable Chromatic Aberration";
            case nameof(GfxConfig.DisableDepthOfField): return "Enable Depth of Field";
            case nameof(GfxConfig.DisableScreenSpaceReflections): return "Enable Screen Space Reflections";
            case nameof(GfxConfig.DisableMotionBlur): return "Enable Motion Blur";
            case nameof(GfxConfig.DisableBloom): return "Enable Bloom";
            case nameof(GfxConfig.DisableFog): return "Enable Fog";
            case nameof(GfxConfig.DisableCloudShadows): return "Enable Cloud Shadows";
            case nameof(GfxConfig.DisableAmbientOcclusion2D): return "Enable Ambient Occlusion 2D";
            case nameof(GfxConfig.DisableBlur): return "Enable Blur";
            case nameof(GfxConfig.DisableSharpen): return "Enable Sharpen";
            case nameof(GfxConfig.DisableVolumetricFog): return "Enable Volumetric Fog";
            case nameof(GfxConfig.DisablePlanarReflections): return "Enable Planar Reflections";
            case nameof(GfxConfig.DisableAura2): return "Enable Aura 2";
            case nameof(GfxConfig.OverrideMode): return "Override Mode";
            case nameof(GfxConfig.EnableSceneSuppression): return "Enable Scene Suppression";
            case nameof(GfxConfig.ShadowDistance): return "Shadow Distance";
            case nameof(GfxConfig.ShadowResolution): return "Shadow Resolution";
            case nameof(GfxConfig.LodBias): return "LOD Bias";
            case nameof(GfxConfig.MSAA): return "MSAA";
            default: return key;
        }
    }

    /// <summary>
    /// One row per bool entry. A <c>Disable*</c> key stores the negative of
    /// what the row shows, so its box is drawn as the enabled state ("Enable
    /// Fog" checked = Fog on); <c>Enable*</c> keys are direct. Only the label
    /// and the box state differ — the stored key and default are unchanged.
    /// A change writes the entry, saves the cfg and sweeps immediately.
    /// </summary>
    private static void DrawToggle(ConfigEntry<bool> entry)
    {
        if (entry == null)
        {
            GUILayout.Label("(config unavailable — see log)");
            return;
        }

        var inverted = entry.Definition.Key.StartsWith("Disable", StringComparison.Ordinal);
        var current = entry.Value;
        var shown = inverted ? !current : current;
        var next = GUILayout.Toggle(shown, Label(entry.Definition.Key));
        if (next != shown)
        {
            entry.Value = inverted ? !next : next;
            ApplyChange();
        }
    }

    /// <summary>
    /// A string-domain override (OverrideMode / [Quality]) is cycled by a
    /// single button. The button shows the option's human-readable label
    /// (e.g. "0.6" → "Fastest"); the cfg still stores the raw spec §4.2 value.
    /// A change writes the entry, saves the cfg and sweeps immediately.
    /// </summary>
    private static void DrawOptionCycle(ConfigEntry<string> entry, (string Value, string Label)[] options)
    {
        if (entry == null)
        {
            GUILayout.Label("(config unavailable — see log)");
            return;
        }

        var current = entry.Value;
        if (GUILayout.Button($"{Label(entry.Definition.Key)} = {DescribeOption(current, options)}"))
        {
            entry.Value = NextOption(current, options);
            ApplyChange();
        }
    }

    /// <summary>Label for a stored value; a hand-edited value not in the list shows verbatim.</summary>
    private static string DescribeOption(string value, (string Value, string Label)[] options)
    {
        foreach (var option in options)
        {
            if (string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return option.Label;
            }
        }

        return value;
    }

    /// <summary>Next value in the cycle; an unrecognised value wraps to the first option.</summary>
    private static string NextOption(string value, (string Value, string Label)[] options)
    {
        var index = -1;
        for (var i = 0; i < options.Length; i++)
        {
            if (string.Equals(options[i].Value, value, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        return options[(index + 1) % options.Length].Value;
    }

    /// <summary>
    /// The scene-pauser master switch. Unlike the generic toggles, flipping it
    /// re-evaluates suppression immediately (off = instant live-3D restore,
    /// on = instant re-capture/suppress of an eligible scene), not just an
    /// effect sweep.
    /// </summary>
    private static void DrawSceneSuppressionToggle()
    {
        var entry = GfxConfig.EnableSceneSuppression;
        if (entry == null)
        {
            GUILayout.Label("(config unavailable — see log)");
            return;
        }

        var current = entry.Value;
        var next = GUILayout.Toggle(current, Label(entry.Definition.Key));
        if (next != current)
        {
            entry.Value = next;
            GfxConfig.Save();
            EvaluateSceneSuppression();
        }
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
        try
        {
            RestoreSceneSuppression("behaviour destroyed");
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] scene suppression teardown failed: {ex}");
        }

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

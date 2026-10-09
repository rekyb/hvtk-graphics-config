using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using UnityEngine.SceneManagement;

namespace GFXConf;

[BepInPlugin("com.rekyb.hvtk.gfxconf", "GFXConf", "0.4.1")]
public class Plugin : BasePlugin
{
    // Strong reference so the managed handler cannot be collected while the
    // native side holds only the converted il2cpp delegate (Review Focus 1).
    private static Action<Scene, LoadSceneMode> _sceneLoadedHandler;
    private static Action<Scene> _sceneUnloadedHandler;

    public override void Load()
    {
        try
        {
            // BasePlugin.Log is an instance member; GfxConfig is static — hand
            // it the log source first so bind failures can still be warned about.
            GfxConfig.LogSource = Log;
            GfxConfig.Bind();
            Log.LogInfo("[GFXConf] v0.4.1 loaded (com.rekyb.hvtk.gfxconf)");
            Log.LogInfo($"[GFXConf] config: {GfxConfig.File?.ConfigFilePath ?? "(config unavailable)"}");

            GfxBehaviour.EnsureCreated();

            var loadedSubscribed = false;
            var unloadedSubscribed = false;

            // Review Focus 1: this interop exposes NO .NET events — the
            // subscription is a direct add_sceneLoaded(UnityAction) call, and
            // the managed→il2cpp delegate conversion inside it
            // (UnityAction.op_Implicit → DelegateSupport.ConvertDelegate) is
            // the known-riskiest line. Isolated so a failure warns instead of
            // aborting Load(); the game must still boot without it.
            try
            {
                _sceneLoadedHandler = OnSceneLoaded;
                SceneManager.add_sceneLoaded(_sceneLoadedHandler);
                loadedSubscribed = true;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[GFXConf] sceneLoaded subscribe failed: {ex}");
            }

            try
            {
                _sceneUnloadedHandler = OnSceneUnloaded;
                SceneManager.add_sceneUnloaded(_sceneUnloadedHandler);
                unloadedSubscribed = true;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[GFXConf] sceneUnloaded subscribe failed: {ex}");
            }

            GfxBehaviour.InitializeSceneSuppression(loadedSubscribed && unloadedSubscribed);

            Sweeper.Request("startup", GfxConfig.DelaySeconds);
        }
        catch (Exception ex)
        {
            try
            {
                Log.LogWarning($"[GFXConf] load failed: {ex}");
            }
            catch
            {
                // Log itself failed — last-resort fallback, never rethrow from Load()
                Console.WriteLine($"[GFXConf] load failed: {ex}");
            }
        }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        try
        {
            // Self-heal: boot-time creation can be destroyed by the game during
            // the first scene transition (proven in testing) — re-ensure the
            // pump once the load has completed.
            GfxBehaviour.EnsureCreated();
            GfxBehaviour.OnSceneLoaded(scene);

            var sceneName = scene.name;
            Sweeper.Request(
                string.IsNullOrEmpty(sceneName) ? $"handle{scene.handle}" : sceneName,
                GfxConfig.DelaySeconds);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[GFXConf] sceneLoaded handler failed: {ex}");
        }
    }

    private void OnSceneUnloaded(Scene scene)
    {
        try
        {
            GfxBehaviour.OnSceneUnloaded(scene);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[GFXConf] sceneUnloaded handler failed: {ex}");
        }
    }
}

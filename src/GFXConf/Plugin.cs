using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace GFXConf;

[BepInPlugin("com.rekyb.hvtk.gfxconf", "GFXConf", "0.1.0")]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        try
        {
            // BasePlugin.Log is an instance member; GfxConfig is static — hand
            // it the log source first so bind failures can still be warned about.
            GfxConfig.LogSource = Log;
            GfxConfig.Bind();
            Log.LogInfo("[GFXConf] v0.1.0 loaded (com.rekyb.hvtk.gfxconf)");
            Log.LogInfo($"[GFXConf] config: {GfxConfig.File?.ConfigFilePath ?? "(config unavailable)"}");
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
}

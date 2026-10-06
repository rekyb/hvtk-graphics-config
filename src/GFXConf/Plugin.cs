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
            Log.LogInfo("[GFXConf] v0.1.0 loaded (com.rekyb.hvtk.gfxconf)");
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

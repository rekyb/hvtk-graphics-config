using System;
using UnityEngine;

namespace GFXConf;

/// <summary>
/// Deferred sweep scheduler (spec §4.1: one schedule line + one summary line
/// per sweep, never per-frame logging).
/// <list type="bullet">
/// <item><see cref="Request"/> — schedule/coalesce (newest pending wins)</item>
/// <item><see cref="Tick"/> — called every frame from <c>GfxBehaviour.Update</c></item>
/// <item><see cref="RunNow"/> — executes when due; skeleton logs one
/// all-zeros summary until Tasks 4/5 fill in the real sweep body.</item>
/// </list>
/// </summary>
internal static class Sweeper
{
    private static bool _pending;
    private static string _sceneLabel;
    private static float _dueTime;

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
    /// Executes the sweep for <paramref name="sceneLabel"/>. This task logs
    /// exactly ONE all-zeros summary line (pinned format — Tasks 4/5 replace
    /// the body but keep the format). Full try/catch: failures become one
    /// warning, never rethrown.
    /// </summary>
    internal static void RunNow(string sceneLabel)
    {
        try
        {
            GfxConfig.LogSource?.LogInfo($"[GFXConf] scene={sceneLabel}: AO=0, CA=0, DoF=0, SSR=0, MB=0, Bloom=0, SCPE.Fog=0, SCPE.CloudShadows=0, SCPE.AO2D=0, SCPE.Blur=0, SCPE.Sharpen=0, VolumetricFog=0, planar=0, aura=0, aa=0");
        }
        catch (Exception ex)
        {
            GfxConfig.LogSource?.LogWarning($"[GFXConf] sweep failed (scene={sceneLabel}): {ex}");
        }
    }
}

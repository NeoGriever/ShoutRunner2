using System;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace ShoutRunner.Runtime;

public sealed class LifestreamBridge(IDalamudPluginInterface pi)
{
    private readonly ICallGateSubscriber<bool> busy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
    private readonly ICallGateSubscriber<string, bool> change = pi.GetIpcSubscriber<string, bool>("Lifestream.ChangeWorld");
    private readonly ICallGateSubscriber<object> abort = pi.GetIpcSubscriber<object>("Lifestream.Abort");
    private readonly ICallGateSubscriber<string, bool> sameDc = pi.GetIpcSubscriber<string, bool>("Lifestream.CanVisitSameDC");
    private readonly ICallGateSubscriber<string, bool> crossDc = pi.GetIpcSubscriber<string, bool>("Lifestream.CanVisitCrossDC");
    private readonly ICallGateSubscriber<uint, byte, bool> teleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
    public bool Available => pi.InstalledPlugins.Any(p => p.InternalName.Equals("Lifestream", StringComparison.OrdinalIgnoreCase) && p.IsLoaded);
    public bool IsBusy()
    {
        try { return Available && busy.InvokeFunc(); }
        catch (Exception ex) { Plugin.Log.Warning($"Lifestream.IsBusy: {ex.Message}"); return false; }
    }
    public bool CanVisit(string world)
    {
        try { return Available && (sameDc.InvokeFunc(world) || crossDc.InvokeFunc(world)); }
        catch { return false; }
    }
    public bool ChangeWorld(string world)
    {
        try { return Available && change.InvokeFunc(world); }
        catch (Exception ex) { Plugin.Log.Warning($"Lifestream.ChangeWorld: {ex.Message}"); return false; }
    }
    public void Abort()
    {
        try { if (Available) abort.InvokeAction(); }
        catch (Exception ex) { Plugin.Log.Warning($"Lifestream.Abort: {ex.Message}"); }
    }
    public bool Teleport(uint aetheryte, byte subIndex)
    {
        try { return Available && teleport.InvokeFunc(aetheryte, subIndex); }
        catch (Exception ex) { Plugin.Log.Warning($"Lifestream.Teleport: {ex.Message}"); return false; }
    }
}

public sealed class NavmeshBridge(IDalamudPluginInterface pi)
{
    private readonly ICallGateSubscriber<bool> ready = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
    private readonly ICallGateSubscriber<bool> moving = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
    private readonly ICallGateSubscriber<bool> finding = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
    private readonly ICallGateSubscriber<Vector3, bool, bool> move = pi.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
    private readonly ICallGateSubscriber<object> stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    public bool Available => pi.InstalledPlugins.Any(p => p.InternalName.Equals("vnavmesh", StringComparison.OrdinalIgnoreCase) && p.IsLoaded);
    public bool IsReady() { try { return Available && ready.InvokeFunc(); } catch { return false; } }
    public bool IsMoving() { try { return Available && moving.InvokeFunc(); } catch { return false; } }
    public bool IsFinding() { try { return Available && finding.InvokeFunc(); } catch { return false; } }
    public bool Move(Vector3 destination) { try { return IsReady() && move.InvokeFunc(destination, false); } catch { return false; } }
    public void Stop() { try { if (Available) stop.InvokeAction(); } catch { } }
}

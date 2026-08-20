using System;
using System.Numerics;
using ECommons.DalamudServices;

namespace MarketHelper;

/// <summary>
/// Minimal IPC bridge to Lifestream, used by the Buyer to hop between worlds on the data center.
///
/// Subscribers are resolved FRESH on every call (never cached) — the same rule the AutoRetainer
/// bridge learned the hard way: a cached subscriber captured before the other plugin finished
/// loading stays dead forever. Every call is wrapped so a missing/updated Lifestream degrades to
/// "not available" instead of throwing into the framework tick.
///
/// Endpoint names match the ones Consolidator already uses in production against Lifestream.
/// </summary>
public static class LifestreamBridge
{
    private const string IpcExecuteCommand = "Lifestream.ExecuteCommand";
    private const string IpcIsBusy = "Lifestream.IsBusy";
    private const string IpcAbort = "Lifestream.Abort";

    /// <summary>True if Lifestream is loaded and its IPC responds.</summary>
    public static bool Available
    {
        get
        {
            try
            {
                Svc.PluginInterface.GetIpcSubscriber<bool>(IpcIsBusy).InvokeFunc();
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>True while Lifestream is mid-travel. Returns false if Lifestream isn't there.</summary>
    public static bool IsBusy()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<bool>(IpcIsBusy).InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Run a Lifestream command exactly as if typed after "/li". Passing a world name makes
    /// Lifestream travel to that world. Returns false if the call couldn't be made at all.
    /// </summary>
    public static bool ExecuteCommand(string argument)
    {
        try
        {
            Svc.PluginInterface.GetIpcSubscriber<string, object>(IpcExecuteCommand).InvokeAction(argument);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Cancel whatever Lifestream is doing. Safe to call when it isn't running.</summary>
    public static void Abort()
    {
        try { Svc.PluginInterface.GetIpcSubscriber<object>(IpcAbort).InvokeAction(); }
        catch { /* not loaded — nothing to abort */ }
    }
}

/// <summary>
/// Minimal IPC bridge to vnavmesh, used only to walk the last stretch to a market board after a
/// world hop. Entirely optional: if vnavmesh isn't installed the Buyer just requires you to be
/// standing near a board already.
/// </summary>
public static class NavmeshBridge
{
    private const string IpcIsReady = "vnavmesh.Nav.IsReady";
    private const string IpcPathfindAndMoveTo = "vnavmesh.SimpleMove.PathfindAndMoveTo";
    private const string IpcPathfindInProgress = "vnavmesh.SimpleMove.PathfindInProgress";
    private const string IpcPathIsRunning = "vnavmesh.Path.IsRunning";
    private const string IpcPathStop = "vnavmesh.Path.Stop";

    /// <summary>True if vnavmesh is loaded and has a usable mesh for the current zone.</summary>
    public static bool Ready
    {
        get
        {
            try { return Svc.PluginInterface.GetIpcSubscriber<bool>(IpcIsReady).InvokeFunc(); }
            catch { return false; }
        }
    }

    /// <summary>True if vnavmesh is loaded at all (regardless of mesh readiness).</summary>
    public static bool Available
    {
        get
        {
            try { Svc.PluginInterface.GetIpcSubscriber<bool>(IpcIsReady).InvokeFunc(); return true; }
            catch { return false; }
        }
    }

    public static bool MoveTo(Vector3 destination)
    {
        try
        {
            return Svc.PluginInterface
                .GetIpcSubscriber<Vector3, bool, bool>(IpcPathfindAndMoveTo)
                .InvokeFunc(destination, false);
        }
        catch { return false; }
    }

    public static bool Moving
    {
        get
        {
            try
            {
                var running = Svc.PluginInterface.GetIpcSubscriber<bool>(IpcPathIsRunning).InvokeFunc();
                var finding = Svc.PluginInterface.GetIpcSubscriber<bool>(IpcPathfindInProgress).InvokeFunc();
                return running || finding;
            }
            catch { return false; }
        }
    }

    public static void Stop()
    {
        try { Svc.PluginInterface.GetIpcSubscriber<object>(IpcPathStop).InvokeAction(); }
        catch { /* not loaded */ }
    }
}

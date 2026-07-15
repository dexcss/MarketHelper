using System;
using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace MarketHelper;

/// <summary>
/// Minimal bridge to AutoRetainer's postprocess API (PunishXIV/AutoRetainerAPI). When AR's
/// multi-mode opens a retainer, it fires OnRetainerReadyForPostprocess after processing ventures
/// and BLOCKS until every registered plugin calls FinishRetainerPostProcess. We register during
/// the "step" event, undercut the open retainer's listings during the "ready" event, then release.
///
/// Because AR waits on our Finish call, ventures are NOT sent until we're done — so this cannot
/// race AR's retainer re-sending. Only the IPC endpoint strings are used (no compiled dependency).
/// </summary>
public sealed class AutoRetainerBridge : IDisposable
{
    // ApiConsts endpoint names from PunishXIV/AutoRetainerAPI.
    private const string OnRetainerReadyForPostprocess = "AutoRetainer.OnRetainerReadyForPostprocess";
    private const string RequestRetainerPostProcess = "AutoRetainer.RequestRetainerPostProcess";
    private const string FinishRetainerPostprocessRequest = "AutoRetainer.FinishRetainerPostprocessRequest";
    private const string OnRetainerPostprocessTask = "AutoRetainer.OnRetainerAdditionalTask";

    private readonly Plugin _plugin;
    private readonly string _pluginName;

    private Dalamud.Plugin.Ipc.ICallGateSubscriber<string, object>? _onStep;
    private Dalamud.Plugin.Ipc.ICallGateSubscriber<string, string, object>? _onReady;
    private Dalamud.Plugin.Ipc.ICallGateSubscriber<string, object>? _request;
    private Dalamud.Plugin.Ipc.ICallGateSubscriber<object>? _finish;

    private bool _subscribed;

    public AutoRetainerBridge(Plugin plugin)
    {
        _plugin = plugin;
        _pluginName = Svc.PluginInterface.InternalName;
    }

    /// <summary>True if AutoRetainer's API is present and responding.</summary>
    public bool AutoRetainerReady
    {
        get
        {
            try
            {
                // Read-only probe: GetMultiModeEnabled returns a bool and has no side effects.
                Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.GetMultiModeEnabled").InvokeFunc();
                return true;
            }
            catch { return false; }
        }
    }

    public void Enable()
    {
        if (_subscribed) return;
        try
        {
            _onStep = Svc.PluginInterface.GetIpcSubscriber<string, object>(OnRetainerPostprocessTask);
            _onReady = Svc.PluginInterface.GetIpcSubscriber<string, string, object>(OnRetainerReadyForPostprocess);
            _request = Svc.PluginInterface.GetIpcSubscriber<string, object>(RequestRetainerPostProcess);
            _finish = Svc.PluginInterface.GetIpcSubscriber<object>(FinishRetainerPostprocessRequest);

            _onStep.Subscribe(OnStep);
            _onReady.Subscribe(OnReady);
            _subscribed = true;
            _plugin.Log.Information("AutoRetainer integration enabled.");
            if (_plugin.Config.Debug)
                _plugin.Chat($"[Market Helper] AR integration subscribed (AR detected: {AutoRetainerReady}). Waiting for retainer events.");
        }
        catch (Exception ex)
        {
            _plugin.Log.Warning($"Couldn't enable AutoRetainer integration: {ex.Message}");
            _plugin.Chat($"[Market Helper] AR integration failed to subscribe: {ex.Message}");
        }
    }

    public void Disable()
    {
        if (!_subscribed) return;
        try
        {
            _onStep?.Unsubscribe(OnStep);
            _onReady?.Unsubscribe(OnReady);
        }
        catch { /* AR may be gone */ }
        _subscribed = false;
    }

    // AR is offering the retainer for postprocessing — register our interest.
    private void OnStep(string retainerName)
    {
        if (_plugin.Config.Debug)
            _plugin.Chat($"[Market Helper] AR OnStep fired for '{retainerName}' (integration on: {_plugin.Config.AutoRetainerIntegration}).");
        if (!_plugin.Config.AutoRetainerIntegration) return;
        try
        {
            _request?.InvokeAction(_pluginName);
            if (_plugin.Config.Debug) _plugin.Chat($"[Market Helper] AR: requested postprocess for '{retainerName}'.");
        }
        catch (Exception ex) { _plugin.Log.Warning($"AR request failed: {ex.Message}"); }
    }

    // It's our turn. AR is blocked until we call finish. Undercut this retainer, then release.
    private void OnReady(string pluginName, string retainerName)
    {
        if (_plugin.Config.Debug)
            _plugin.Chat($"[Market Helper] AR OnReady fired (for plugin '{pluginName}', me: '{_pluginName}').");
        if (pluginName != _pluginName) return;   // not addressed to us
        if (!_plugin.Config.AutoRetainerIntegration) { Finish(); return; }

        // Scope filters (opt-out: empty list = act on all). If both lists are set, BOTH must pass.
        var cfg = _plugin.Config;
        if (cfg.ArOnlyCharacters.Count > 0)
        {
            var chara = Player.Available ? Player.Name : string.Empty;
            if (!cfg.ArOnlyCharacters.Any(c => string.Equals(c, chara, StringComparison.OrdinalIgnoreCase)))
            {
                _plugin.Log.Information($"AR: skipping {retainerName} — character '{chara}' not in allow-list.");
                Finish();
                return;
            }
        }
        if (cfg.ArOnlyRetainers.Count > 0)
        {
            if (!cfg.ArOnlyRetainers.Any(r => string.Equals(r, retainerName, StringComparison.OrdinalIgnoreCase)))
            {
                _plugin.Log.Information($"AR: skipping retainer '{retainerName}' — not in allow-list.");
                Finish();
                return;
            }
        }

        // Undercut this retainer's listings; when done, optionally auto-list preset items, then
        // release AR. Chained so AR is only released after BOTH complete.
        _plugin.Nav.StartPostprocess(retainerName, () =>
        {
            if (_plugin.Config.ArAutoList && _plugin.AllListerItems().Count > 0)
                _plugin.Lister.StartPostprocess(retainerName, Finish);
            else
                Finish();
        });
    }

    private void Finish()
    {
        try { _finish?.InvokeAction(); }
        catch (Exception ex) { _plugin.Log.Warning($"AR finish failed: {ex.Message}"); }
    }

    public void Dispose() => Disable();
}

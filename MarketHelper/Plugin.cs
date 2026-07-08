using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using MarketHelper.Windows;
using ECommons;

namespace MarketHelper;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public IFramework Framework { get; private set; } = null!;
    [PluginService] public IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] public ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] public IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public IPluginLog Log { get; private set; } = null!;
    [PluginService] public IClientState ClientState { get; private set; } = null!;

    public Configuration Config { get; }
    public MarketListener Listener { get; }
    public NavRunner Nav { get; }
    public ListRunner Lister { get; }
    public AutoRetainerBridge ArBridge { get; }

    // Session-only Lister items (in memory; cleared automatically on plugin reload / game close,
    // and via the manual "Clear session" button). Separate from Config.ListerItems (permanent).
    public readonly List<uint> SessionListerItems = new();

    /// <summary>Permanent + session items to auto-list, de-duplicated, in a stable order.</summary>
    public List<uint> AllListerItems()
    {
        var all = new List<uint>(Config.ListerItems);
        foreach (var id in SessionListerItems)
            if (!all.Contains(id)) all.Add(id);
        return all;
    }

    /// <summary>Remove a listed item from whichever list(s) hold it (session and/or permanent).</summary>
    public void RemoveListerItem(uint id)
    {
        var changed = Config.ListerItems.Remove(id);
        SessionListerItems.Remove(id);
        if (changed) Config.Save();
    }

    private readonly WindowSystem _windows = new("MarketHelper");
    private readonly MainWindow _mainWindow;

    private const string Command = "/undercut";
    private static readonly string[] OpenAliases = { "/markethelp", "/market", "/mh" };

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);
        Listener = new MarketListener(this);
        Nav = new NavRunner(this);
        Lister = new ListRunner(this);
        ArBridge = new AutoRetainerBridge(this);
        if (Config.AutoRetainerIntegration) ArBridge.Enable();

        _mainWindow = new MainWindow(this);
        _windows.AddWindow(_mainWindow);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Market Helper. Subcommands: run / stop / now / dump.",
        });
        foreach (var alias in OpenAliases)
        {
            CommandManager.AddHandler(alias, new CommandInfo(OnCommand)
            {
                HelpMessage = alias == "/markethelp" ? "Open Market Helper." : "Open Market Helper (alias)." ,
            });
        }

        PluginInterface.UiBuilder.Draw += _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMain;
        Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _) { Nav.Tick(); Lister.Tick(); }

    private void OnCommand(string command, string args)
    {
        args = args.Trim().ToLowerInvariant();
        if (args == "run")
        {
            Nav.Start();
            _mainWindow.IsOpen = true;
        }
        else if (args == "stop")
        {
            Nav.Stop();
        }
        else if (args == "now")
        {
            Listener.PriceOpenItemNow();
            _mainWindow.IsOpen = true;
        }
        else if (args == "dump")
        {
            // Diagnostic: dump RetainerSellList row node trees to find the mannequin icon node.
            if (!Addons.Exists("RetainerSellList"))
            {
                Chat("[Market Helper] Open a retainer's sell list first, then run /undercut dump.");
            }
            else
            {
                var count = RetainerReader.ActiveMarketItems();
                Chat($"[Market Helper] Dumping {count} rows:");
                Chat($"[Market Helper] {Addons.DumpSellListRow(-1)}");
                for (var i = 0; i < count && i < 20; i++)
                    Chat($"[Market Helper] row {i}: mannequin={Addons.IsSellListRowMannequin(i)}");
            }
        }
        else
        {
            OpenMain();
        }
    }

    private void OpenMain() => _mainWindow.IsOpen = true;

    public void Chat(string message) => ChatGui.Print(message);

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        Listener.Dispose();
        PluginInterface.UiBuilder.Draw -= _windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMain;
        _windows.RemoveAllWindows();
        ArBridge.Dispose();
        CommandManager.RemoveHandler(Command);
        foreach (var alias in OpenAliases)
            CommandManager.RemoveHandler(alias);
        ECommonsMain.Dispose();
    }
}

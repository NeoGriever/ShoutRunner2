using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ShoutRunner.Runtime;
using ShoutRunner.Windows;

namespace ShoutRunner;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private readonly WindowSystem windows = new("ShoutRunner");
    private readonly MainWindow main;
    private readonly StatusWindow statusWindow;
    private readonly LifestreamBridge lifestream;
    private readonly NavmeshBridge navmesh;
    private readonly TradeInterceptor trades;
    private bool resumeChecked;
    public Config Configuration { get; }
    public RouteExecutor Executor { get; }

    public Plugin()
    {
        Configuration = Config.Load();
        WorldCatalog.Load(); AetheryteCatalog.Load();
        lifestream = new LifestreamBridge(PluginInterface);
        navmesh = new NavmeshBridge(PluginInterface);
        Executor = new RouteExecutor(Configuration, lifestream, navmesh);
        trades = new TradeInterceptor(Configuration, () => Executor.IsTeleporting, Executor.OnTradeInterrupted);
        main = new MainWindow(Configuration, Executor, lifestream, navmesh, trades);
        statusWindow = new StatusWindow(Configuration, Executor);
        main.ToggleStatusWindow = () => statusWindow.IsOpen = !statusWindow.IsOpen;
        main.StatusWindowOpen = () => statusWindow.IsOpen;
        windows.AddWindow(main);
        windows.AddWindow(statusWindow);
        Framework.Update += OnUpdate;
        ToastGui.ErrorToast += OnErrorToast;
        CommandManager.AddHandler("/sr", new CommandInfo(OnCommand) { HelpMessage = "Open ShoutRunner or use /sr run, /sr stop." });
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += main.Open;
        PluginInterface.UiBuilder.OpenConfigUi += main.Open;
        Log.Information("ShoutRunner2 loaded.");
    }
    private void OnUpdate(IFramework _)
    {
        trades.Tick();
        if (resumeChecked || !PlayerState.IsLoaded || !lifestream.Available) return;
        resumeChecked = true;
        if (Configuration.AutoResumePresetId is { } id)
        {
            var preset = Configuration.Presets.Find(p => p.Id == id);
            if (preset != null) Executor.Start(preset);
            else { Configuration.AutoResumePresetId = null; Configuration.Save(); }
        }
    }
    private void OnErrorToast(ref Dalamud.Game.Text.SeStringHandling.SeString message, ref bool handled) => Executor.OnTravelError(message.TextValue);
    private void OnCommand(string _, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "run": Executor.Start(Configuration.Active); break;
            case "stop": Executor.Stop(); break;
            default: main.Open(); break;
        }
    }
    public static bool SendChatCommand(string command) => ChatBox.Send(command);
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= main.Open;
        PluginInterface.UiBuilder.OpenConfigUi -= main.Open;
        CommandManager.RemoveHandler("/sr");
        Framework.Update -= OnUpdate;
        ToastGui.ErrorToast -= OnErrorToast;
        trades.Dispose(); Executor.Dispose();
        windows.RemoveAllWindows();
    }
}

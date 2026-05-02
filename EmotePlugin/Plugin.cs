using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using EmotePlugin.Windows;

namespace EmotePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private const string CommandName = "/emotes";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("EmotePlugin");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private QuickAccessWindow QuickAccessWindow { get; init; }

    private PenumbraService PenumbraService { get; init; }
    private EmoteManager EmoteManager { get; init; }

    public Plugin(IClientState clientState)
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();

        PenumbraService = new PenumbraService(PluginInterface, Log);
        EmoteManager = new EmoteManager(Configuration, PenumbraService, Log);

        var emoteIconHelper = new EmoteIconHelper(TextureProvider, DataManager);
        ConfigWindow = new ConfigWindow(this, PenumbraService);
        MainWindow = new MainWindow(this, EmoteManager, PenumbraService, emoteIconHelper);
        QuickAccessWindow = new QuickAccessWindow(this, EmoteManager, PenumbraService, emoteIconHelper, clientState, Condition);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(QuickAccessWindow);

        if (Configuration.ShowQuickAccess)
            QuickAccessWindow.IsOpen = true;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Emote Plugin window",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"===Emote Plugin loaded===");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        QuickAccessWindow.Dispose();
        PenumbraService.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var emote = EmoteManager.GetAllEmotes().FirstOrDefault(
                e => e.Alias.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (emote != null)
            {
                EmoteManager.UseEmote(emote);
                return;
            }
        }

        MainWindow.Toggle();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    public void ToggleQuickAccess()
    {
        QuickAccessWindow.Toggle();
        Configuration.ShowQuickAccess = QuickAccessWindow.IsOpen;
        Configuration.Save();
    }

    public void SetQuickAccessVisible(bool visible)
    {
        QuickAccessWindow.IsOpen = visible;
    }
}

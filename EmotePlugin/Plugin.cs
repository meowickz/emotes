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
    private MainWindow MainWindow { get; init; }
    private QuickAccessWindow QuickAccessWindow { get; init; }
    private WhatsNewWindow WhatsNewWindow { get; init; }

    private PenumbraService PenumbraService { get; init; }
    private EmoteManager EmoteManager { get; init; }

    public Plugin(IClientState clientState)
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Migrate();

        PenumbraService = new PenumbraService(PluginInterface, Log);
        EmoteManager = new EmoteManager(Configuration, PenumbraService, Log);

        var emoteIconHelper = new EmoteIconHelper(TextureProvider, DataManager);
        MainWindow = new MainWindow(this, EmoteManager, PenumbraService, emoteIconHelper);
        QuickAccessWindow = new QuickAccessWindow(this, EmoteManager, PenumbraService, emoteIconHelper, clientState, Condition);
        WhatsNewWindow = new WhatsNewWindow();

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(QuickAccessWindow);
        WindowSystem.AddWindow(WhatsNewWindow);

        if (Configuration.ShowQuickAccess)
            QuickAccessWindow.IsOpen = true;

        ShowWhatsNewOnUpdate();

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Emote Plugin window",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += MainWindow.DrawOverlays;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;

        Log.Information($"===Emote Plugin loaded===");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= MainWindow.DrawOverlays;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        QuickAccessWindow.Dispose();
        WhatsNewWindow.Dispose();
        PenumbraService.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            var matchedDisabled = false;
            foreach (var emote in EmoteManager.GetAllEmotes())
            {
                var cmd = emote.Commands.FirstOrDefault(
                    c => c.Alias.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
                if (cmd == null)
                    continue;
                if (cmd.Enabled)
                {
                    EmoteManager.UseEmote(emote, cmd);
                    return;
                }

                matchedDisabled = true;
            }

            // A disabled alias should not fall through to toggling the window (macros!)
            if (matchedDisabled)
            {
                Log.Warning($"Alias '{trimmed}' matches a disabled command — enable it in the emote editor to use it.");
                return;
            }
        }

        MainWindow.Toggle();
    }

    public void ToggleConfigUi() => MainWindow.ToggleSettings();
    public void ShowWhatsNew() => WhatsNewWindow.IsOpen = true;

    private void ShowWhatsNewOnUpdate()
    {
        // Gate on the changelog's own latest entry, not the assembly version —
        // a release without new notes must not re-show stale ones.
        var currentVersion = WhatsNewWindow.LatestVersion;
        if (Configuration.LastSeenVersion == currentVersion)
            return;

        // Only show for updates, not fresh installs: an existing config has emotes or a recorded version.
        var isUpdate = !string.IsNullOrEmpty(Configuration.LastSeenVersion) ||
                       EmoteManager.GetAllEmotes().Count > 0;
        if (isUpdate)
            WhatsNewWindow.IsOpen = true;

        Configuration.LastSeenVersion = currentVersion;
        Configuration.Save();
    }
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

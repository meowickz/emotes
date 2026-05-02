using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace EmotePlugin.Windows;

public class QuickAccessWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly EmoteManager emoteManager;
    private readonly PenumbraService penumbraService;
    private readonly EmoteIconHelper emoteIconHelper;
    private readonly IClientState clientState;
    private readonly ICondition condition;

    private string filterQuery = string.Empty;
    private int selectedIndex = -1;
    private bool comboOpen;

    public QuickAccessWindow(Plugin plugin, EmoteManager emoteManager, PenumbraService penumbraService,
        EmoteIconHelper emoteIconHelper, IClientState clientState, ICondition condition)
        : base("Quick Access###EmoteQuickAccess",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(200, 0),
            MaximumSize = new Vector2(1000, 50),
        };

        RespectCloseHotkey = false;

        this.plugin = plugin;
        this.emoteManager = emoteManager;
        this.penumbraService = penumbraService;
        this.emoteIconHelper = emoteIconHelper;
        this.clientState = clientState;
        this.condition = condition;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (!clientState.IsLoggedIn)
            return;

        if (condition[ConditionFlag.InCombat] ||
            condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78])
            return;

        // Dark background for controls (like Glamourer's toolbar)
        var dark = new Vector4(0.20f, 0.20f, 0.20f, 0.75f);
        var darkHover = new Vector4(0.30f, 0.30f, 0.30f, 0.85f);
        var darkActive = new Vector4(0.15f, 0.15f, 0.15f, 0.90f);
        ImGui.PushStyleColor(ImGuiCol.Button, dark);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, darkHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, darkActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, dark);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, darkHover);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, darkActive);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, ImGui.GetStyle().FrameRounding);

        var emotes = emoteManager.GetAllEmotes();
        if (emotes.Count == 0)
        {
            ImGui.TextDisabled("No emotes configured.");
            ImGui.PopStyleColor(6);
            ImGui.PopStyleVar();
            return;
        }

        // Clamp selected index
        if (selectedIndex < 0 || selectedIndex >= emotes.Count)
            selectedIndex = 0;

        var selected = emotes[selectedIndex];

        // Combo with filter
        ImGui.SetNextItemWidth(250f);
        if (ImGui.BeginCombo("##QuickEmoteSelect", selected.Name))
        {
            // Filter input
            ImGui.SetNextItemWidth(-1);
            if (!comboOpen)
            {
                ImGui.SetKeyboardFocusHere();
                comboOpen = true;
            }
            ImGui.InputTextWithHint("##QuickFilter", "Filter...", ref filterQuery, 256);

            for (var i = 0; i < emotes.Count; i++)
            {
                var emote = emotes[i];

                // Apply filter
                if (!string.IsNullOrEmpty(filterQuery) &&
                    !emote.Name.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) &&
                    !emote.EmoteCommand.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) &&
                    !emote.Alias.Contains(filterQuery, StringComparison.OrdinalIgnoreCase))
                    continue;

                var isSelected = i == selectedIndex;
                if (ImGui.Selectable(emote.Name, isSelected))
                {
                    selectedIndex = i;
                    filterQuery = string.Empty;
                }

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        else
        {
            comboOpen = false;
        }

        ImGui.SameLine();

        // Play button
        if (ImGuiComponents.IconButton("##QuickPlay", FontAwesomeIcon.Play))
        {
            emoteManager.UseEmote(selected);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Play emote");

        ImGui.SameLine();

        // Disable all mods button
        if (ImGuiComponents.IconButton("##QuickDisable", FontAwesomeIcon.Ban))
        {
            emoteManager.DisableAllEmoteMods();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Disable all temporary mods");

        ImGui.SameLine();

        // Open main window button
        if (ImGuiComponents.IconButton("##QuickMain", FontAwesomeIcon.List))
        {
            plugin.ToggleMainUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open main window");

        ImGui.SameLine();

        // Settings button
        if (ImGuiComponents.IconButton("##QuickSettings", FontAwesomeIcon.Cog))
        {
            plugin.ToggleConfigUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open settings");

        ImGui.PopStyleColor(6);
        ImGui.PopStyleVar();
    }
}

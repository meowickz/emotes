using System;
using System.Collections.Generic;
using System.Linq;
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
    private bool comboOpen;
    private float wheelAccumulator;

    // Selection tracked by identity, not position — the item list changes shape
    private Guid selectedEmoteId;
    private Guid selectedCmdId;

    // Item list cached across frames; rebuilt when the config changes
    private List<QuickItem>? cachedItems;
    private int cachedRevision = -1;
    private bool cachedShowSub;

    private sealed record QuickItem(EmoteEntry Emote, EmoteCommandEntry? Cmd, string Label);

    private List<QuickItem> GetItems()
    {
        var showSub = plugin.Configuration.QuickAccessShowSubCommands;
        if (cachedItems == null || cachedRevision != plugin.Configuration.Revision || cachedShowSub != showSub)
        {
            cachedItems = BuildItems(showSub);
            cachedRevision = plugin.Configuration.Revision;
            cachedShowSub = showSub;
        }

        return cachedItems;
    }

    private List<QuickItem> BuildItems(bool showSub)
    {
        var items = new List<QuickItem>();

        foreach (var emote in emoteManager.GetAllEmotes())
        {
            if (showSub)
            {
                var enabledCmds = emote.Commands.Where(c => c.Enabled).ToList();
                if (enabledCmds.Count > 1)
                {
                    foreach (var cmd in enabledCmds)
                    {
                        var subName = string.IsNullOrWhiteSpace(cmd.Name) ? cmd.Command : cmd.Name;
                        items.Add(new QuickItem(emote, cmd, $"{emote.Name} · {subName}"));
                    }

                    continue;
                }
            }

            items.Add(new QuickItem(emote, null, emote.Name));
        }

        return items;
    }

    private void SetSelected(QuickItem item)
    {
        selectedEmoteId = item.Emote.Id;
        selectedCmdId = item.Cmd?.Id ?? Guid.Empty;
    }

    private bool MatchesFilter(QuickItem item)
    {
        if (item.Emote.Name.Contains(filterQuery, StringComparison.OrdinalIgnoreCase))
            return true;

        if (item.Cmd != null)
            return item.Cmd.Name.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) ||
                   item.Cmd.Command.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) ||
                   item.Cmd.Alias.Contains(filterQuery, StringComparison.OrdinalIgnoreCase);

        return item.Emote.Commands.Any(c =>
            c.Command.Contains(filterQuery, StringComparison.OrdinalIgnoreCase) ||
            c.Alias.Contains(filterQuery, StringComparison.OrdinalIgnoreCase));
    }

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

        var items = GetItems();
        if (items.Count == 0)
        {
            ImGui.TextDisabled("No emotes configured.");
            ImGui.PopStyleColor(6);
            ImGui.PopStyleVar();
            return;
        }

        // Resolve the stored selection by identity; fall back to the first item
        var selectedIndex = items.FindIndex(i =>
            i.Emote.Id == selectedEmoteId && (i.Cmd?.Id ?? Guid.Empty) == selectedCmdId);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
            SetSelected(items[0]);
        }

        var selected = items[selectedIndex];

        var buttonSize = new Vector2(ImGui.GetFrameHeight());
        var buttonGap = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        var comboWidth = 250f;

        // Combo with filter; pin the popup to the combo width so it doesn't open wide and shrink
        ImGui.SetNextItemWidth(comboWidth);
        ImGui.SetNextWindowSizeConstraints(new Vector2(comboWidth, 0), new Vector2(comboWidth, 300f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginCombo("##QuickEmoteSelect", selected.Label))
        {
            // Filter pinned above the scrolling list — flush and square (Glamourer-style)
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
            ImGui.SetNextItemWidth(-1);
            if (!comboOpen)
            {
                ImGui.SetKeyboardFocusHere();
                comboOpen = true;
            }
            ImGui.InputTextWithHint("##QuickFilter", "Filter...", ref filterQuery, 256);
            ImGui.PopStyleVar();

            if (ImGui.BeginChild("##QuickItems", new Vector2(-1, 240)))
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];

                    if (!string.IsNullOrEmpty(filterQuery) && !MatchesFilter(item))
                        continue;

                    var isSelected = i == selectedIndex;
                    if (ImGui.Selectable($"{item.Label}##{i}", isSelected))
                    {
                        SetSelected(item);
                        filterQuery = string.Empty;
                        // Selectables inside a child window don't auto-close the popup
                        ImGui.CloseCurrentPopup();
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndChild();
            ImGui.EndCombo();
        }
        else
        {
            comboOpen = false;
        }

        ImGui.PopStyleVar();

        // Mouse wheel over the closed combo cycles the selection.
        // Accumulate fractional deltas so precision touchpads/free-spin wheels work.
        if (ImGui.IsItemHovered())
        {
            wheelAccumulator += ImGui.GetIO().MouseWheel;
            var steps = (int)wheelAccumulator;
            if (steps != 0)
            {
                wheelAccumulator -= steps;
                var newIndex = Math.Clamp(selectedIndex - steps, 0, items.Count - 1);
                SetSelected(items[newIndex]);
            }
        }
        else
        {
            wheelAccumulator = 0;
        }

        ImGui.SameLine(0, buttonGap);

        // Play button
        if (ImGuiComponents.IconButton("##QuickPlay", FontAwesomeIcon.Play, buttonSize))
        {
            if (selected.Cmd != null)
                emoteManager.UseEmote(selected.Emote, selected.Cmd);
            else
                emoteManager.UseEmote(selected.Emote);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Play emote");

        ImGui.SameLine(0, buttonGap);

        // Disable all mods button
        if (ImGuiComponents.IconButton("##QuickDisable", FontAwesomeIcon.Ban, buttonSize))
        {
            emoteManager.DisableAllEmoteMods();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Disable all temporary mods");

        ImGui.SameLine(0, buttonGap);

        // Open main window button
        if (ImGuiComponents.IconButton("##QuickMain", FontAwesomeIcon.List, buttonSize))
        {
            plugin.ToggleMainUi();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open main window");

        ImGui.PopStyleColor(6);
        ImGui.PopStyleVar();
    }
}

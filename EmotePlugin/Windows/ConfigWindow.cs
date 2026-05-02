using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmotePlugin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly PenumbraService penumbraService;

    public ConfigWindow(Plugin plugin, PenumbraService penumbraService)
        : base("Emote Plugin Settings###EmotePluginConfig")
    {
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(350, 150);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        this.penumbraService = penumbraService;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Text("Penumbra Integration");
        ImGui.Separator();

        var available = penumbraService.Available;
        var enabled = available && penumbraService.IsPenumbraEnabled();

        ImGui.Text("Status:");
        ImGui.SameLine();
        if (!available)
            ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), "Not Available");
        else if (!enabled)
            ImGui.TextColored(new Vector4(1, 1, 0, 1), "Disabled");
        else
            ImGui.TextColored(new Vector4(0, 1, 0, 1), "Connected");

        if (ImGui.Button("Refresh Penumbra Connection"))
            penumbraService.CheckAvailability();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text($"Emotes configured: {plugin.Configuration.Emotes.Count}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var showQuickAccess = plugin.Configuration.ShowQuickAccess;
        if (ImGui.Checkbox("Show Quick Access Widget", ref showQuickAccess))
        {
            plugin.Configuration.ShowQuickAccess = showQuickAccess;
            plugin.Configuration.Save();
            plugin.SetQuickAccessVisible(showQuickAccess);
        }

        var alwaysRedraw = plugin.Configuration.AlwaysRedraw;
        if (ImGui.Checkbox("Always Redraw on Emote Use", ref alwaysRedraw))
        {
            plugin.Configuration.AlwaysRedraw = alwaysRedraw;
            plugin.Configuration.Save();
        }
    }
}

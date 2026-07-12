using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace EmotePlugin.Windows;

public class WhatsNewWindow : Window, IDisposable
{
    private static readonly (string Version, string[] Changes)[] Changelog =
    {
        ("1.3.0.0", new[]
        {
            "Animation preview: the eye button on a command row plays the animation locally with mods applied — only you see it; your character is locked until Stop Preview.",
            "Mod library scanner: scan all installed Penumbra mods for emote animations and bulk-import them as emotes, with rename/skip/destination-folder review.",
            "Standing idle poses (/changepose) now support direct pose selection, detection, and preview.",
            "Conflict icons are priority-aware: green when this mod wins its shared emote slots, yellow when another mod overrides it.",
            "Quick Access follows the sidebar's folder ordering, and all filter dropdowns pin the search box on top (Glamourer-style).",
        }),
        ("1.2.0.0", new[]
        {
            "Pose selection: sit, ground sit and doze commands can specify a /cpose pose number and land directly in that pose — playing the row again while already sitting/dozing switches the pose in place.",
            "Detect from mods now also reads which pose slots a mod replaces and adds pose-ready command rows (e.g. \"Doze Pose 3\").",
            "Sit/Doze Anywhere: optional setting to play /sit and /doze without a chair or bed, using the game's hidden furniture emote.",
            "Import is now selective: tick/untick individual emotes in a combined review table (mods, remapping and folder location shown per emote).",
            "Importing folders with names you already have merges them instead of creating duplicates.",
        }),
        ("1.1.0.0", new[]
        {
            "Emotes can now hold multiple commands, each with its own name, alias, enabled toggle and a default marker. Reorder them with the arrow buttons.",
            "Detect from mods: scan an emote's associated Penumbra mods and add the emote commands they change automatically (facial expressions are sorted last).",
            "Inline mod settings: view and edit each associated mod's option groups directly in the emote editor, applied live as temporary settings.",
            "Conflict warnings: mods sharing the same emote slot — including mods enabled permanently in the collection — are flagged with a warning icon.",
            "Import/export emote sets as JSON, with a review dialog to remap or drop missing mods on import.",
            "Quick Access: sub-command entries (configurable), mouse-wheel selection, square buttons and a tighter layout.",
            "Settings moved into a tab in the main window; the separate settings window was removed.",
        }),
    };

    /// <summary> The newest version that has changelog content — gates the auto-popup. </summary>
    public static string LatestVersion => Changelog[0].Version;

    public WhatsNewWindow()
        : base("Emote Plugin — What's New###EmotePluginWhatsNew")
    {
        Size = new Vector2(520, 400);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Center on screen whenever the window (re)appears
        var viewport = ImGui.GetMainViewport();
        var center = viewport.Pos + viewport.Size * 0.5f;
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    public override void Draw()
    {
        var footerHeight = ImGui.GetFrameHeightWithSpacing();
        if (ImGui.BeginChild("##WhatsNewContent", new Vector2(-1, -footerHeight)))
        {
            for (var i = 0; i < Changelog.Length; i++)
            {
                var (version, changes) = Changelog[i];
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), $"Version {version}");
                ImGui.Spacing();

                foreach (var change in changes)
                {
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.TextWrapped(change);
                }

                if (i < Changelog.Length - 1)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();
                }
            }
        }

        ImGui.EndChild();

        const float buttonWidth = 120f;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - buttonWidth) * 0.5f);
        if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
            IsOpen = false;
    }
}

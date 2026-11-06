using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace EmotePlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    /// <summary> Bumped on every Save; lets UI caches detect config changes without polling the tree. </summary>
    [Newtonsoft.Json.JsonIgnore]
    public int Revision { get; private set; }

    // Legacy flat list — kept for backwards compatibility during deserialization
    public List<EmoteEntry> Emotes { get; set; } = new();

    public EmoteFolder RootFolder { get; set; } = new() { Name = "Root" };

    public string LastSeenVersion { get; set; } = string.Empty;

    public bool ShowQuickAccess { get; set; } = true;

    public bool QuickAccessShowSubCommands { get; set; } = true;

    public bool AlwaysRedraw { get; set; } = false;

    /// <summary> Play /sit and /doze via the game's hidden furniture emote — works without a chair/bed. </summary>
    public bool SitDozeAnywhere { get; set; } = false;

    /// <summary>
    /// After sitting/dozing down, correct the pose with automatic /cpose should the game land on
    /// another one. Off: the pose is only preselected. (Changing pose while already seated always
    /// uses /cpose — nothing else changes the current pose.)
    /// </summary>
    public bool AutoPoseCorrection { get; set; } = false;

    /// <summary>
    /// Locally replace sit/groundsit/doze pose transitions (and lying awake before dozing off)
    /// with the selected pose's loop. Other players still see the transitions.
    /// </summary>
    public bool SkipPoseTransitions { get; set; } = true;

    /// <summary> Log the character's emote/pose/animation state for 10s after each emote use. </summary>
    public bool TraceEmotes { get; set; } = false;

    /// <summary> With TraceEmotes: also sample the body animation clips (time/length/blend weight) every 100ms. </summary>
    public bool TraceAnimation { get; set; } = false;

    /// <summary>
    /// Mod directories the mod scanner has listed in an import. Lets a later scan tell mods that
    /// are truly new apart from ones the user already saw and chose not to import.
    /// </summary>
    public List<string> ScannedModDirectories { get; set; } = new();

    /// <summary>
    /// Migrate legacy formats: flat Emotes list into RootFolder tree,
    /// and single command/alias fields into per-emote command lists.
    /// </summary>
    public void Migrate()
    {
        var changed = false;

        if (Emotes.Count > 0)
        {
            RootFolder.Emotes.AddRange(Emotes);
            Emotes.Clear();
            changed = true;
        }

        if (MigrateAllCommands(RootFolder))
            changed = true;

        if (changed)
            Save();
    }

    /// <summary> Migrate legacy command/alias fields for every emote in the tree. Shared with the import path. </summary>
    public static bool MigrateAllCommands(EmoteFolder root)
    {
        var changed = false;
        foreach (var emote in root.EnumerateEmotes())
            changed |= emote.MigrateCommands();
        return changed;
    }

    public void Save()
    {
        Revision++;
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

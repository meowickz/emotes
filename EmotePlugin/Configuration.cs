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

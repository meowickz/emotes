using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace EmotePlugin;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Legacy flat list — kept for backwards compatibility during deserialization
    public List<EmoteEntry> Emotes { get; set; } = new();

    public EmoteFolder RootFolder { get; set; } = new() { Name = "Root" };

    public bool ShowQuickAccess { get; set; } = true;

    public bool AlwaysRedraw { get; set; } = false;

    /// <summary>
    /// Migrate legacy flat Emotes list into RootFolder tree structure.
    /// </summary>
    public void Migrate()
    {
        if (Emotes.Count > 0)
        {
            RootFolder.Emotes.AddRange(Emotes);
            Emotes.Clear();
            Save();
        }
    }

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

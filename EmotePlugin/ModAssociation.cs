using System;
using System.Collections.Generic;
using System.Linq;

namespace EmotePlugin;

[Serializable]
public class ModAssociation
{
    public string ModDirectory { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool Inherit { get; set; }
    public int Priority { get; set; } = 1;
    public Dictionary<string, List<string>> Settings { get; set; } = new();

    public ModAssociation Clone()
    {
        return new ModAssociation
        {
            ModDirectory = ModDirectory,
            ModName = ModName,
            Enabled = Enabled,
            Inherit = Inherit,
            Priority = Priority,
            Settings = Settings.ToDictionary(k => k.Key, k => new List<string>(k.Value)),
        };
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace EmotePlugin;

[Serializable]
public class EmoteEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string EmoteCommand { get; set; } = string.Empty;
    public Guid PenumbraCollectionId { get; set; } = Guid.Empty;
    public bool AutoToggleMod { get; set; } = true;
    public List<ModAssociation> AssociatedMods { get; set; } = new();

    public EmoteEntry Clone()
    {
        return new EmoteEntry
        {
            Id = Guid.NewGuid(),
            Name = Name + " (Copy)",
            Alias = string.Empty,
            EmoteCommand = EmoteCommand,
            PenumbraCollectionId = PenumbraCollectionId,
            AutoToggleMod = AutoToggleMod,
            AssociatedMods = AssociatedMods.Select(m => m.Clone()).ToList(),
        };
    }
}

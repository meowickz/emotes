using System;
using System.Collections.Generic;
using System.Linq;

namespace EmotePlugin;

[Serializable]
public class EmoteCommandEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary> 1-based /cpose pose to select before playing (sit/groundsit/doze only); 0 = keep current. </summary>
    public int PoseIndex { get; set; }

    public EmoteCommandEntry Clone()
    {
        return new EmoteCommandEntry
        {
            Id = Guid.NewGuid(),
            Name = Name,
            Command = Command,
            Alias = string.Empty,
            Enabled = Enabled,
            PoseIndex = PoseIndex,
        };
    }
}

[Serializable]
public class EmoteEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<EmoteCommandEntry> Commands { get; set; } = new();
    public Guid DefaultCommandId { get; set; } = Guid.Empty;
    public Guid PenumbraCollectionId { get; set; } = Guid.Empty;
    public bool AutoToggleMod { get; set; } = true;
    public List<ModAssociation> AssociatedMods { get; set; } = new();

    // Legacy single command/alias — kept for deserialization of old configs and exports
    public string Alias { get; set; } = string.Empty;
    public string EmoteCommand { get; set; } = string.Empty;

    /// <summary>
    /// Migrate legacy EmoteCommand/Alias into the Commands list. Returns true if anything changed.
    /// </summary>
    public bool MigrateCommands()
    {
        if (string.IsNullOrWhiteSpace(EmoteCommand) && string.IsNullOrWhiteSpace(Alias))
            return false;

        // Merge rather than discard when a Commands list already exists alongside legacy fields
        var normalized = EmoteCommand.TrimStart('/');
        var existing = Commands.FirstOrDefault(c =>
            c.Command.TrimStart('/').Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            var row = new EmoteCommandEntry { Command = EmoteCommand, Alias = Alias };
            Commands.Add(row);
            if (DefaultCommandId == Guid.Empty)
                DefaultCommandId = row.Id;
        }
        else if (string.IsNullOrWhiteSpace(existing.Alias) && !string.IsNullOrWhiteSpace(Alias))
        {
            existing.Alias = Alias;
        }

        EmoteCommand = string.Empty;
        Alias = string.Empty;
        return true;
    }

    public EmoteCommandEntry? GetDefaultCommand()
    {
        return Commands.FirstOrDefault(c => c.Enabled && c.Id == DefaultCommandId)
               ?? Commands.FirstOrDefault(c => c.Enabled);
    }

    public EmoteEntry Clone()
    {
        var clone = new EmoteEntry
        {
            Id = Guid.NewGuid(),
            Name = Name + " (Copy)",
            PenumbraCollectionId = PenumbraCollectionId,
            AutoToggleMod = AutoToggleMod,
            AssociatedMods = AssociatedMods.Select(m => m.Clone()).ToList(),
        };

        foreach (var cmd in Commands)
        {
            var cmdClone = cmd.Clone();
            clone.Commands.Add(cmdClone);
            if (cmd.Id == DefaultCommandId)
                clone.DefaultCommandId = cmdClone.Id;
        }

        return clone;
    }
}

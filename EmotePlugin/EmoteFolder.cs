using System;
using System.Collections.Generic;

namespace EmotePlugin;

[Serializable]
public class EmoteFolder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public List<EmoteFolder> Folders { get; set; } = new();
    public List<EmoteEntry> Emotes { get; set; } = new();

    /// <summary> This folder and all nested subfolders, depth-first. </summary>
    public IEnumerable<EmoteFolder> SelfAndDescendants()
    {
        yield return this;
        foreach (var sub in Folders)
        foreach (var folder in sub.SelfAndDescendants())
            yield return folder;
    }

    /// <summary> All emotes in this folder and all nested subfolders. </summary>
    public IEnumerable<EmoteEntry> EnumerateEmotes()
    {
        foreach (var folder in SelfAndDescendants())
        foreach (var emote in folder.Emotes)
            yield return emote;
    }
}

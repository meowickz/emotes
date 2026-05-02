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
}

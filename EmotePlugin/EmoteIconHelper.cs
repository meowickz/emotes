using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace EmotePlugin;

public class EmoteIconHelper
{
    private readonly Dictionary<string, uint> iconCache = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly ITextureProvider textureProvider;

    public EmoteIconHelper(ITextureProvider textureProvider, IDataManager dataManager)
    {
        this.textureProvider = textureProvider;
        BuildCache(dataManager);
    }

    private void BuildCache(IDataManager dataManager)
    {
        var emoteSheet = dataManager.GetExcelSheet<Emote>();
        if (emoteSheet == null) return;

        foreach (var emote in emoteSheet)
        {
            if (emote.Icon == 0) continue;
            var textCommand = emote.TextCommand.ValueNullable;
            if (textCommand == null) continue;

            var cmd = textCommand.Value.Command.ExtractText();
            if (!string.IsNullOrEmpty(cmd))
                iconCache.TryAdd(cmd, emote.Icon);

            var alias = textCommand.Value.Alias.ExtractText();
            if (!string.IsNullOrEmpty(alias))
                iconCache.TryAdd(alias, emote.Icon);

            var shortCmd = textCommand.Value.ShortCommand.ExtractText();
            if (!string.IsNullOrEmpty(shortCmd))
                iconCache.TryAdd(shortCmd, emote.Icon);

            var shortAlias = textCommand.Value.ShortAlias.ExtractText();
            if (!string.IsNullOrEmpty(shortAlias))
                iconCache.TryAdd(shortAlias, emote.Icon);
        }
    }

    public void DrawIcon(string emoteCommand, float size)
    {
        var cmd = emoteCommand;
        if (!string.IsNullOrWhiteSpace(cmd) && !cmd.StartsWith('/'))
            cmd = "/" + cmd;

        if (string.IsNullOrWhiteSpace(cmd) || !iconCache.TryGetValue(cmd, out var iconId))
            return;

        var tex = textureProvider.GetFromGameIcon(new GameIconLookup(iconId));
        var wrap = tex.GetWrapOrEmpty();
        if (wrap.Size.X > 0)
        {
            ImGui.Image(wrap.Handle, new Vector2(size, size));
            ImGui.SameLine();
        }
    }
}

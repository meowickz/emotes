using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace EmotePlugin;

public class EmoteIconHelper
{
    private const uint ExpressionsCategoryRowId = 3;

    private readonly Dictionary<string, uint> iconCache = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> nameToCommand = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expressionCommands = new(System.StringComparer.OrdinalIgnoreCase);
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
            {
                iconCache.TryAdd(cmd, emote.Icon);

                var emoteName = emote.Name.ExtractText();
                if (!string.IsNullOrEmpty(emoteName))
                    nameToCommand.TryAdd(emoteName, cmd);

                if (emote.EmoteCategory.RowId == ExpressionsCategoryRowId)
                    expressionCommands.Add(cmd);
            }

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

    /// <summary>
    /// Resolve a Penumbra changed-item name (e.g. "Emote: Sundrop Dance") to an emote
    /// slash command plus the cleaned display name. Null when it is not an emote.
    /// </summary>
    public (string Command, string Name)? ResolveEmote(string changedItemName)
    {
        var name = changedItemName.Trim();
        if (name.StartsWith("Emote:", System.StringComparison.OrdinalIgnoreCase))
            name = name["Emote:".Length..].Trim();

        if (nameToCommand.TryGetValue(name, out var cmd))
            return (cmd, name);

        // Some changed items may already be a command string
        if (name.StartsWith('/') && iconCache.ContainsKey(name))
            return (name, name);

        return null;
    }

    /// <summary> Whether the given slash command is a facial expression emote. </summary>
    public bool IsExpressionCommand(string command)
    {
        var cmd = command.Trim();
        if (!cmd.StartsWith('/'))
            cmd = "/" + cmd;
        return expressionCommands.Contains(cmd);
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

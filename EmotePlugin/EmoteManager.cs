using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace EmotePlugin;

public class EmoteManager
{
    private readonly Configuration configuration;
    private readonly PenumbraService penumbraService;
    private readonly IPluginLog log;

    private EmoteEntry? lastUsedEmote;

    public EmoteManager(Configuration configuration, PenumbraService penumbraService, IPluginLog log)
    {
        this.configuration = configuration;
        this.penumbraService = penumbraService;
        this.log = log;
    }

    public EmoteFolder GetRootFolder() => configuration.RootFolder;

    public List<EmoteEntry> GetAllEmotes()
    {
        var result = new List<EmoteEntry>();
        CollectEmotes(configuration.RootFolder, result);
        return result;
    }

    private void CollectEmotes(EmoteFolder folder, List<EmoteEntry> result)
    {
        result.AddRange(folder.Emotes);
        foreach (var sub in folder.Folders)
            CollectEmotes(sub, result);
    }

    public List<EmoteEntry> SearchEmotes(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetAllEmotes();

        return GetAllEmotes()
            .Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.EmoteCommand.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public EmoteEntry AddEmote(string name, string emoteCommand = "", EmoteFolder? folder = null)
    {
        folder ??= configuration.RootFolder;
        var entry = new EmoteEntry
        {
            Name = name,
            EmoteCommand = emoteCommand,
        };
        folder.Emotes.Add(entry);
        configuration.Save();
        return entry;
    }

    public void RemoveEmote(EmoteEntry emote)
    {
        if (lastUsedEmote?.Id == emote.Id)
            lastUsedEmote = null;
        var folder = FindParentFolder(emote);
        folder?.Emotes.Remove(emote);
        configuration.Save();
    }

    public EmoteEntry DuplicateEmote(EmoteEntry emote)
    {
        var clone = emote.Clone();
        var folder = FindParentFolder(emote) ?? configuration.RootFolder;
        var index = folder.Emotes.IndexOf(emote);
        folder.Emotes.Insert(index + 1, clone);
        configuration.Save();
        return clone;
    }

    public void RenameEmote(EmoteEntry emote, string newName)
    {
        emote.Name = newName;
        configuration.Save();
    }

    public void UpdateEmote(EmoteEntry emote)
    {
        configuration.Save();
    }

    public void UseEmote(EmoteEntry emote)
    {
        if (string.IsNullOrWhiteSpace(emote.EmoteCommand))
        {
            log.Warning($"Emote '{emote.Name}' has no command set.");
            return;
        }

        // Remove previous emote's temporary mod settings before applying new ones
        if (emote.AutoToggleMod)
        {
            var lastCmd = lastUsedEmote?.EmoteCommand?.TrimStart('/');
            var normalizedCmd = emote.EmoteCommand.TrimStart('/');
            var needsRedraw = configuration.AlwaysRedraw ||
                (lastCmd != null && lastCmd.Equals(normalizedCmd, StringComparison.OrdinalIgnoreCase));

            if (lastUsedEmote != null && lastUsedEmote.Id != emote.Id)
                DisableEmoteMods(lastUsedEmote, skipRedraw: !needsRedraw);

            foreach (var mod in emote.AssociatedMods)
            {
                penumbraService.ApplyTemporaryModSettings(
                    mod.ModDirectory, mod.Enabled, mod.Inherit, mod.Priority,
                    emote.PenumbraCollectionId, mod.ModName, mod.Settings);
            }

            lastUsedEmote = emote;

            // Only redraw if the previous emote used the same command (same animation source)
            if (needsRedraw)
            {
                penumbraService.RedrawSelf();
            }
        }

        // Send the emote command via chat
        var command = emote.EmoteCommand;
        if (!command.StartsWith('/'))
            command = "/" + command;

        SendChatCommand(command);
        log.Information($"Used emote: {emote.Name} ({command})");
    }

    private static unsafe void SendChatCommand(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null) return;

        using var message = new FFXIVClientStructs.FFXIV.Client.System.String.Utf8String(command);
        uiModule->ProcessChatBoxEntry(&message, nint.Zero, false);
    }

    public void DisableEmoteMods(EmoteEntry emote, bool skipRedraw = false)
    {
        foreach (var mod in emote.AssociatedMods)
        {
            penumbraService.RemoveTemporarySettings(mod.ModDirectory, emote.PenumbraCollectionId, mod.ModName);
        }
        if (!skipRedraw)
            penumbraService.RedrawSelf();
    }

    public void ApplyAllModSettings(EmoteEntry emote)
    {
        foreach (var mod in emote.AssociatedMods)
        {
            penumbraService.ApplyTemporaryModSettings(
                mod.ModDirectory, mod.Enabled, mod.Inherit, mod.Priority,
                emote.PenumbraCollectionId, mod.ModName, mod.Settings);
        }
        penumbraService.RedrawSelf();
    }

    public void ApplyModSetting(EmoteEntry emote, ModAssociation mod)
    {
        penumbraService.ApplyTemporaryModSettings(
            mod.ModDirectory, mod.Enabled, mod.Inherit, mod.Priority,
            emote.PenumbraCollectionId, mod.ModName, mod.Settings);
        penumbraService.RedrawSelf();
    }

    public void ReapplyModFromPenumbra(EmoteEntry emote, ModAssociation mod)
    {
        var (enabled, priority, settings) = penumbraService.GetModSettings(
            mod.ModDirectory, emote.PenumbraCollectionId, mod.ModName);
        mod.Enabled = enabled;
        mod.Priority = priority;
        mod.Settings = settings;
        configuration.Save();
    }

    public void MoveEmote(EmoteEntry emote, int direction)
    {
        var folder = FindParentFolder(emote);
        if (folder == null) return;
        var fromIndex = folder.Emotes.IndexOf(emote);
        if (fromIndex < 0) return;
        var toIndex = fromIndex + direction;
        if (toIndex < 0 || toIndex >= folder.Emotes.Count) return;

        folder.Emotes.RemoveAt(fromIndex);
        folder.Emotes.Insert(toIndex, emote);
        configuration.Save();
    }

    public void MoveEmoteTo(EmoteEntry emote, int targetIndex)
    {
        var folder = FindParentFolder(emote);
        if (folder == null) return;
        var fromIndex = folder.Emotes.IndexOf(emote);
        if (fromIndex < 0) return;
        targetIndex = Math.Clamp(targetIndex, 0, folder.Emotes.Count - 1);
        if (fromIndex == targetIndex) return;

        folder.Emotes.RemoveAt(fromIndex);
        folder.Emotes.Insert(targetIndex, emote);
        configuration.Save();
    }

    public void MoveEmoteToFolderAt(EmoteEntry emote, EmoteFolder targetFolder, int targetIndex)
    {
        var sourceFolder = FindParentFolder(emote);
        if (sourceFolder == null) return;
        sourceFolder.Emotes.Remove(emote);
        targetIndex = Math.Clamp(targetIndex, 0, targetFolder.Emotes.Count);
        targetFolder.Emotes.Insert(targetIndex, emote);
        configuration.Save();
    }

    public EmoteFolder? FindParentFolder(EmoteEntry emote, EmoteFolder? searchIn = null)
    {
        searchIn ??= configuration.RootFolder;
        if (searchIn.Emotes.Contains(emote))
            return searchIn;
        foreach (var sub in searchIn.Folders)
        {
            var found = FindParentFolder(emote, sub);
            if (found != null) return found;
        }
        return null;
    }

    public EmoteFolder? FindParentOfFolder(EmoteFolder folder, EmoteFolder? searchIn = null)
    {
        searchIn ??= configuration.RootFolder;
        if (searchIn.Folders.Contains(folder))
            return searchIn;
        foreach (var sub in searchIn.Folders)
        {
            var found = FindParentOfFolder(folder, sub);
            if (found != null) return found;
        }
        return null;
    }

    public EmoteFolder AddFolder(string name, EmoteFolder? parent = null)
    {
        parent ??= configuration.RootFolder;
        var folder = new EmoteFolder { Name = name };
        parent.Folders.Add(folder);
        configuration.Save();
        return folder;
    }

    public void RemoveFolder(EmoteFolder folder)
    {
        var parent = FindParentOfFolder(folder);
        if (parent == null) return;

        // Move children up to parent (non-destructive)
        parent.Emotes.AddRange(folder.Emotes);
        parent.Folders.AddRange(folder.Folders);

        parent.Folders.Remove(folder);
        configuration.Save();
    }

    public void RenameFolder(EmoteFolder folder, string newName)
    {
        folder.Name = newName;
        configuration.Save();
    }

    public void MoveFolderToFolder(EmoteFolder folder, EmoteFolder targetParent)
    {
        // Prevent moving a folder into itself or a descendant
        if (folder.Id == targetParent.Id || IsDescendantOf(targetParent, folder))
            return;

        var currentParent = FindParentOfFolder(folder);
        if (currentParent == null || currentParent.Id == targetParent.Id)
            return;

        currentParent.Folders.Remove(folder);
        targetParent.Folders.Add(folder);
        configuration.Save();
    }

    public bool IsDescendantOf(EmoteFolder potentialDescendant, EmoteFolder ancestor)
    {
        foreach (var sub in ancestor.Folders)
        {
            if (sub.Id == potentialDescendant.Id)
                return true;
            if (IsDescendantOf(potentialDescendant, sub))
                return true;
        }
        return false;
    }

    public void DisableAllEmoteMods()
    {
        foreach (var e in GetAllEmotes())
        {
            foreach (var mod in e.AssociatedMods)
            {
                penumbraService.RemoveTemporarySettings(mod.ModDirectory, e.PenumbraCollectionId, mod.ModName);
            }
        }
        lastUsedEmote = null;
        penumbraService.RedrawSelf();
    }
}

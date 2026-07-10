using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Newtonsoft.Json;

namespace EmotePlugin;

[Serializable]
public class EmoteSetExport
{
    public int Version { get; set; } = 1;
    public EmoteFolder Root { get; set; } = new();
}

public class EmoteManager
{
    private readonly Configuration configuration;
    private readonly PenumbraService penumbraService;
    private readonly IPluginLog log;

    private EmoteEntry? lastUsedEmote;
    private string? lastUsedCommand;

    public EmoteManager(Configuration configuration, PenumbraService penumbraService, IPluginLog log)
    {
        this.configuration = configuration;
        this.penumbraService = penumbraService;
        this.log = log;
    }

    public EmoteFolder GetRootFolder() => configuration.RootFolder;

    public List<EmoteEntry> GetAllEmotes()
        => configuration.RootFolder.EnumerateEmotes().ToList();

    public int GetEmoteCount()
        => CountEmotes(configuration.RootFolder);

    private static int CountEmotes(EmoteFolder folder)
        => folder.Emotes.Count + folder.Folders.Sum(CountEmotes);

    public List<EmoteEntry> SearchEmotes(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return GetAllEmotes();

        return GetAllEmotes()
            .Where(e => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.Commands.Any(c => c.Command.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                            c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                            c.Alias.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public EmoteEntry AddEmote(string name, string emoteCommand = "", EmoteFolder? folder = null)
    {
        folder ??= configuration.RootFolder;
        var entry = new EmoteEntry { Name = name };
        if (!string.IsNullOrWhiteSpace(emoteCommand))
        {
            var row = new EmoteCommandEntry { Command = emoteCommand };
            entry.Commands.Add(row);
            entry.DefaultCommandId = row.Id;
        }
        folder.Emotes.Add(entry);
        configuration.Save();
        return entry;
    }

    public void RemoveEmote(EmoteEntry emote)
    {
        if (lastUsedEmote?.Id == emote.Id)
        {
            lastUsedEmote = null;
            lastUsedCommand = null;
        }
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
        var cmd = emote.GetDefaultCommand();
        if (cmd == null)
        {
            log.Warning($"Emote '{emote.Name}' has no enabled command.");
            return;
        }

        UseEmote(emote, cmd);
    }

    public void UseEmote(EmoteEntry emote, EmoteCommandEntry cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd.Command))
        {
            log.Warning($"Emote '{emote.Name}' has no command set.");
            return;
        }

        // Remove previous emote's temporary mod settings before applying new ones
        if (emote.AutoToggleMod)
        {
            var lastCmd = lastUsedCommand?.TrimStart('/');
            var normalizedCmd = cmd.Command.TrimStart('/');
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
            lastUsedCommand = cmd.Command;

            // Only redraw if the previous emote used the same command (same animation source)
            if (needsRedraw)
            {
                penumbraService.RedrawSelf();
            }
        }

        // Send the emote command via chat
        var command = cmd.Command;
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

    /// <summary>
    /// Copy the mod's current option settings from Penumbra into the association,
    /// leaving enabled state and priority untouched.
    /// </summary>
    public void SyncModSettingsFromPenumbra(EmoteEntry emote, ModAssociation mod)
    {
        var (_, _, settings) = penumbraService.GetModSettings(
            mod.ModDirectory, emote.PenumbraCollectionId, mod.ModName);
        if (settings.Count == 0)
            return;

        mod.Settings = settings;
        configuration.Save();
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

    public string ExportToJson()
    {
        return JsonConvert.SerializeObject(
            new EmoteSetExport { Root = configuration.RootFolder }, Formatting.Indented);
    }

    public static EmoteFolder? ParseImport(string json)
    {
        var data = JsonConvert.DeserializeObject<EmoteSetExport>(json);
        if (data?.Root == null)
            return null;

        // Sanitize immediately — the import review UI walks this tree before ImportEmotes runs
        SanitizeImport(data.Root);
        return data.Root;
    }

    public void ImportEmotes(EmoteFolder importedRoot)
    {
        SanitizeImport(importedRoot);
        Configuration.MigrateAllCommands(importedRoot);
        RegenerateIds(importedRoot);
        ClearDuplicateAliases(importedRoot);
        configuration.RootFolder.Emotes.AddRange(importedRoot.Emotes);
        configuration.RootFolder.Folders.AddRange(importedRoot.Folders);
        configuration.Save();
    }

    /// <summary>
    /// Repair null fields/entries in externally produced import files so they can't
    /// crash later or persist nulls into the config.
    /// </summary>
    private static void SanitizeImport(EmoteFolder folder)
    {
        folder.Name ??= string.Empty;
        folder.Folders ??= new List<EmoteFolder>();
        folder.Emotes ??= new List<EmoteEntry>();
        folder.Folders.RemoveAll(f => f == null);
        folder.Emotes.RemoveAll(e => e == null);

        foreach (var emote in folder.Emotes)
        {
            emote.Name ??= string.Empty;
            emote.Alias ??= string.Empty;
            emote.EmoteCommand ??= string.Empty;
            emote.Commands ??= new List<EmoteCommandEntry>();
            emote.Commands.RemoveAll(c => c == null);
            foreach (var cmd in emote.Commands)
            {
                cmd.Name ??= string.Empty;
                cmd.Command ??= string.Empty;
                cmd.Alias ??= string.Empty;
            }

            emote.AssociatedMods ??= new List<ModAssociation>();
            emote.AssociatedMods.RemoveAll(m => m == null);
            foreach (var mod in emote.AssociatedMods)
            {
                mod.ModDirectory ??= string.Empty;
                mod.ModName ??= string.Empty;
                mod.Settings ??= new Dictionary<string, List<string>>();
                foreach (var key in mod.Settings.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList())
                    mod.Settings[key] = new List<string>();
            }
        }

        foreach (var sub in folder.Folders)
            SanitizeImport(sub);
    }

    /// <summary>
    /// Aliases must stay unique for /emotes resolution — clear imported aliases that
    /// collide with existing ones (or repeat within the import itself).
    /// </summary>
    private void ClearDuplicateAliases(EmoteFolder importedRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var emote in configuration.RootFolder.EnumerateEmotes())
        foreach (var cmd in emote.Commands)
        {
            if (!string.IsNullOrWhiteSpace(cmd.Alias))
                seen.Add(cmd.Alias);
        }

        foreach (var emote in importedRoot.EnumerateEmotes())
        foreach (var cmd in emote.Commands)
        {
            if (string.IsNullOrWhiteSpace(cmd.Alias))
                continue;
            if (!seen.Add(cmd.Alias))
            {
                log.Warning($"Imported alias '{cmd.Alias}' on '{emote.Name}' already exists — cleared to keep /emotes unambiguous.");
                cmd.Alias = string.Empty;
            }
        }
    }

    private static void RegenerateIds(EmoteFolder root)
    {
        foreach (var folder in root.SelfAndDescendants())
        {
            folder.Id = Guid.NewGuid();
            foreach (var emote in folder.Emotes)
            {
                emote.Id = Guid.NewGuid();
                foreach (var cmd in emote.Commands)
                {
                    var oldId = cmd.Id;
                    cmd.Id = Guid.NewGuid();
                    if (emote.DefaultCommandId == oldId)
                        emote.DefaultCommandId = cmd.Id;
                }
            }
        }
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
        lastUsedCommand = null;
        penumbraService.RedrawSelf();
    }
}

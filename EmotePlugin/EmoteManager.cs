using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Newtonsoft.Json;

namespace EmotePlugin;

[Serializable]
public class EmoteSetExport
{
    public int Version { get; set; } = 1;
    public EmoteFolder Root { get; set; } = new();
}

public class EmoteManager : IDisposable
{
    private readonly Configuration configuration;
    private readonly PenumbraService penumbraService;
    private readonly EmoteIconHelper emoteData;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private EmoteEntry? lastUsedEmote;
    private string? lastUsedCommand;

    // A pose change waiting for the character to enter the right stance
    private (byte PoseType, byte TargetIndex, DateTime Deadline)? pendingPose;

    public EmoteManager(Configuration configuration, PenumbraService penumbraService, EmoteIconHelper emoteData,
        IFramework framework, IObjectTable objectTable, IPluginLog log)
    {
        this.configuration = configuration;
        this.penumbraService = penumbraService;
        this.emoteData = emoteData;
        this.framework = framework;
        this.objectTable = objectTable;
        this.log = log;

        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        // Never leave the character anim-locked by a running preview
        StopPreview();
        framework.Update -= OnFrameworkUpdate;
    }

    public EmoteFolder GetRootFolder() => configuration.RootFolder;

    public List<EmoteEntry> GetAllEmotes()
        => configuration.RootFolder.EnumerateEmotesDisplayOrder().ToList();

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

    /// <summary> Add a fully built emote entry (e.g. from the mod scanner). </summary>
    public void AddEmote(EmoteEntry entry, EmoteFolder? folder = null)
    {
        (folder ?? configuration.RootFolder).Emotes.Add(entry);
        configuration.Save();
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

        // An anim-locked preview would block the real emote
        StopPreview();

        // Remove previous emote's temporary mod settings before applying new ones
        if (emote.AutoToggleMod)
        {
            var lastCmd = lastUsedCommand?.TrimStart('/');
            var normalizedCmd = cmd.Command.TrimStart('/');
            var needsRedraw = configuration.AlwaysRedraw ||
                (lastCmd != null && lastCmd.Equals(normalizedCmd, StringComparison.OrdinalIgnoreCase));

            if (lastUsedEmote != null && lastUsedEmote.Id != emote.Id)
                DisableEmoteMods(lastUsedEmote, skipRedraw: !needsRedraw);

            ApplyAssociatedMods(emote);

            lastUsedEmote = emote;
            lastUsedCommand = cmd.Command;

            // Only redraw if the previous emote used the same command (same animation source)
            if (needsRedraw)
            {
                penumbraService.RedrawSelf();
            }
        }

        var poseType = cmd.PoseIndex > 0 ? emoteData.GetPoseType(cmd.Command) : null;

        // Only switch the pose (no command sent) when already in the target stance —
        // re-sending the emote would cancel it (e.g. /doze while dozing stands up) —
        // or for standing idle poses, where /changepose IS the cycle command and
        // sending it raw would move the pose before the watcher corrects it.
        var currentPoseType = GetLocalPoseState()?.PoseType;
        if (poseType != null && (poseType.Value == 0 || currentPoseType == poseType.Value))
        {
            QueuePoseChange(poseType.Value, (byte)(cmd.PoseIndex - 1));
            log.Information(currentPoseType == poseType.Value
                ? $"Used emote: {emote.Name} (pose change to {cmd.PoseIndex})"
                : $"Emote {emote.Name}: idle pose {cmd.PoseIndex} queued — applies when standing (5s timeout).");
            return;
        }

        // With Sit/Doze Anywhere enabled, execute the game's hidden furniture emote
        // instead of the chat command, so sit/doze work without a chair or bed nearby.
        var anywhereId = configuration.SitDozeAnywhere ? emoteData.GetAnywhereEmoteId(cmd.Command) : null;
        if (anywhereId != null)
        {
            ExecuteHotbarEmote(anywhereId.Value);
            log.Information($"Used emote: {emote.Name} (anywhere emote {anywhereId.Value})");
        }
        else
        {
            // Send the emote command via chat
            var command = cmd.Command;
            if (!command.StartsWith('/'))
                command = "/" + command;

            SendChatCommand(command);
            log.Information($"Used emote: {emote.Name} ({command})");
        }

        // Once the stance is entered, cycle /cpose to the requested pose
        if (poseType != null)
            QueuePoseChange(poseType.Value, (byte)(cmd.PoseIndex - 1));
    }

    // Saved character state while a local preview is running (mode, mode param, base override)
    private (FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes Mode, byte ModeParam, ushort BaseOverride)? previewOriginalState;
    private System.Numerics.Vector3 previewStartPosition;

    /// <summary> Whether a local preview animation is currently active. </summary>
    public bool IsPreviewing => previewOriginalState != null;

    /// <summary>
    /// Play this command's animation on the local player only — a client-side base-override
    /// playback (the Brio mechanism) that the server never sees, so other players see
    /// nothing. The character is anim-locked until StopPreview restores it. The emote's
    /// mods are applied first (no redraw needed: animation files load when playback starts).
    /// Returns false when no timeline could be resolved.
    /// </summary>
    public unsafe bool PreviewEmote(EmoteEntry emote, EmoteCommandEntry cmd)
    {
        var timelineId = emoteData.GetPreviewTimeline(cmd.Command, cmd.PoseIndex);
        if (timelineId == null)
        {
            log.Warning($"No preview animation found for '{cmd.Command}' (pose {cmd.PoseIndex}).");
            return false;
        }

        // Capture the player once — LocalPlayer can go null between accesses
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero)
            return false;

        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)localPlayer.Address;

        // A queued pose change must not fire /cpose into the anim lock
        pendingPose = null;

        // Track preview-applied mods exactly like a real use, so the next
        // emote's cleanup pass removes them
        if (emote.AutoToggleMod)
        {
            if (lastUsedEmote != null && lastUsedEmote.Id != emote.Id)
                DisableEmoteMods(lastUsedEmote, skipRedraw: true);

            ApplyAssociatedMods(emote);
            lastUsedEmote = emote;
            lastUsedCommand = null;
        }

        // Keep the FIRST original state when chaining previews
        previewOriginalState ??= (chara->Mode, chara->ModeParam, chara->Timeline.BaseOverride);
        previewStartPosition = localPlayer.Position;

        chara->SetMode(FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.AnimLock, 0);
        chara->Timeline.BaseOverride = timelineId.Value;
        chara->Timeline.TimelineSequencer.PlayTimeline(timelineId.Value, null);

        log.Information($"Previewing emote: {emote.Name} (timeline {timelineId.Value}, local only)");
        return true;
    }

    private void ApplyAssociatedMods(EmoteEntry emote)
    {
        foreach (var mod in emote.AssociatedMods)
        {
            penumbraService.ApplyTemporaryModSettings(
                mod.ModDirectory, mod.Enabled, mod.Inherit, mod.Priority,
                emote.PenumbraCollectionId, mod.ModName, mod.Settings);
        }
    }

    /// <summary> Cancel a running local preview and restore the character's normal state. </summary>
    public unsafe void StopPreview()
    {
        if (previewOriginalState == null)
            return;

        var original = previewOriginalState.Value;
        previewOriginalState = null;

        var chara = GetLocalCharacter();
        if (chara == null)
            return;

        chara->Timeline.BaseOverride = original.BaseOverride;
        // Restore through SetMode — a raw field write leaves the game's mode
        // machinery thinking we're still anim-locked ("operating a siege machine")
        chara->SetMode(original.Mode, original.ModeParam);
        chara->Timeline.TimelineSequencer.PlayTimeline(3, null); // 3 = normal idle blend
    }

    /// <summary>
    /// Preview safety net, per framework tick: if the character moved or something else
    /// (combat, knockback, mounting, zoning) took over the character mode while a preview
    /// was active, release the preview instead of fighting or clobbering the new state.
    /// </summary>
    private unsafe void WatchPreview()
    {
        if (previewOriginalState == null)
            return;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero)
        {
            // Logged out / zoning — the character is gone, nothing to restore
            previewOriginalState = null;
            return;
        }

        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)localPlayer.Address;

        if (chara->Mode != FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.AnimLock)
        {
            // The game changed the mode out from under us — accept the new mode,
            // but still clear our animation override so it can't linger
            var original = previewOriginalState.Value;
            previewOriginalState = null;
            chara->Timeline.BaseOverride = original.BaseOverride;
            return;
        }

        if (System.Numerics.Vector3.DistanceSquared(localPlayer.Position, previewStartPosition) > 0.01f)
            StopPreview(); // movement broke through the lock — restore cleanly
    }

    private unsafe FFXIVClientStructs.FFXIV.Client.Game.Character.Character* GetLocalCharacter()
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero)
            return null;

        return (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)localPlayer.Address;
    }

    /// <summary> Execute an emote by id through the hotbar module's scratch slot. </summary>
    private static unsafe void ExecuteHotbarEmote(uint emoteId)
    {
        var hotbarModule = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()
            ->UIModule->GetRaptureHotbarModule();
        if (hotbarModule == null)
            return;

        hotbarModule->ScratchSlot.Set(RaptureHotbarModule.HotbarSlotType.Emote, emoteId);
        hotbarModule->ExecuteSlot(&hotbarModule->ScratchSlot);
        hotbarModule->ScratchSlot.Set(RaptureHotbarModule.HotbarSlotType.Empty, 0);
    }

    private void QueuePoseChange(byte poseType, byte targetIndex)
        => pendingPose = (poseType, targetIndex, DateTime.UtcNow.AddSeconds(5));

    /// <summary> The local player's current pose stance and pose index, or null when unavailable. </summary>
    private unsafe (byte PoseType, byte PoseIndex)? GetLocalPoseState()
    {
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero)
            return null;

        var chara = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)localPlayer.Address;
        return ((byte)chara->EmoteController.CurrentPoseType, chara->EmoteController.CPoseState);
    }

    /// <summary>
    /// Applies a queued pose change once the character has entered the target stance:
    /// sends /cpose the exact number of times needed to land on the requested pose
    /// (the approach used by pose plugins — the game offers no direct set).
    /// </summary>
    private void OnFrameworkUpdate(IFramework _)
    {
        WatchPreview();

        if (pendingPose == null)
            return;

        var (poseType, targetIndex, deadline) = pendingPose.Value;
        if (DateTime.UtcNow > deadline)
        {
            pendingPose = null;
            return;
        }

        var state = GetLocalPoseState();
        if (state == null || state.Value.PoseType != poseType)
            return; // stance not entered yet — keep waiting until the deadline

        // Per Poser: GetAvailablePoses returns the highest valid pose index
        var totalPoses = EmoteController.GetAvailablePoses((EmoteController.PoseType)poseType) + 1;
        var current = state.Value.PoseIndex;
        var target = Math.Min(targetIndex, totalPoses - 1);

        var steps = ((target - current) % totalPoses + totalPoses) % totalPoses;
        for (var i = 0; i < steps; i++)
            SendChatCommand("/cpose");

        if (steps > 0)
            log.Debug($"Cycled /cpose {steps}x to pose {target + 1} (type {poseType}).");
        pendingPose = null;
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
        ApplyAssociatedMods(emote);
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
        MergeInto(configuration.RootFolder, importedRoot);
        configuration.Save();
    }

    /// <summary> Merge the source tree into the target, combining same-named folders (case-insensitive). </summary>
    private static void MergeInto(EmoteFolder target, EmoteFolder source)
    {
        target.Emotes.AddRange(source.Emotes);
        foreach (var sub in source.Folders)
        {
            var existing = target.Folders.FirstOrDefault(
                f => f.Name.Equals(sub.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                MergeInto(existing, sub);
            else
                target.Folders.Add(sub);
        }
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
                cmd.PoseIndex = Math.Clamp(cmd.PoseIndex, 0, 9);
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

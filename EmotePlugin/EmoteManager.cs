using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
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
    // What the last use applied (see ModSettingsKey) — a redraw only helps when this changes
    private string? lastAppliedModsKey;

    // The last emote actually played: its command, and its mod settings when it played modded
    // (null = vanilla). Only a directly following play of the same command with another mod's
    // settings collides with the still-loaded animation and needs a redraw — another emote in
    // between, vanilla -> mod and mod -> vanilla all load fresh.
    private (string Command, string? ModsKey)? lastPlayed;
    // The draw object that play belongs to: a new one (zoning, any redraw) reloaded everything
    private nint lastPlayedDrawObject;

    // A pose change waiting for the character to enter the right stance, stepped one /cpose at a time
    private PendingPose? pendingPose;

    private sealed class PendingPose
    {
        public required byte PoseType;
        public required byte TargetIndex;
        public DateTime Deadline;
        // Pose index when the last /cpose was sent, and when — null before the first step
        public byte? SentFromIndex;
        public DateTime SentAt;
        // When the stance was first seen entered — the pose index is not reliable right away
        public DateTime? InStanceSince;
    }

    // Wait this long after entering the stance before the first /cpose
    private static readonly TimeSpan PoseStanceSettle = TimeSpan.FromSeconds(0.5);
    // Lying awake (doze entered via a pose emote) is a settled state: /cpose right away, and
    // resend quickly — every moment there shows the vanilla lie-down loop
    private static readonly TimeSpan PoseStepRetryLyingAwake = TimeSpan.FromMilliseconds(250);

    // Resend a /cpose that didn't change the pose after this long (dropped mid-transition)
    private static readonly TimeSpan PoseStepRetry = TimeSpan.FromSeconds(1.2);

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
        penumbraService.LocalPlayerRedrawn += OnLocalPlayerRedrawn;
    }

    public void Dispose()
    {
        // Never leave the character anim-locked by a running preview
        StopPreview();
        // Nor holding a pose loop as its base animation
        StopTransitionSkip();
        framework.Update -= OnFrameworkUpdate;
        penumbraService.LocalPlayerRedrawn -= OnLocalPlayerRedrawn;
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

    /// <summary>
    /// Config v2: the mod scanner numbered doze pose rows by file name (v0: l_pose0N = pose
    /// N+1, v1: pose N), but the game orders doze poses as bed_liedown, l_pose02, l_pose01.
    /// Renumber the rows it created, recognizable by their generated "Doze Pose N" name.
    /// </summary>
    public void MigrateDozePoseNumbers()
    {
        if (configuration.Version >= 2)
            return;

        var dozeName = emoteData.GetPoseTypeInfo(4)?.Name;
        var changed = 0;
        foreach (var emote in configuration.RootFolder.EnumerateEmotes())
        {
            foreach (var cmd in emote.Commands)
            {
                if (dozeName == null || cmd.PoseIndex < 1 || emoteData.GetPoseType(cmd.Command) != 4 ||
                    cmd.Name != $"{dozeName} Pose {cmd.PoseIndex}")
                    continue;

                var file = configuration.Version == 0 ? cmd.PoseIndex - 1 : cmd.PoseIndex;
                if (file < 1 || emoteData.GetPoseForAnimationKey($"emote/l_pose{file:00}_loop") is not { PoseType: 4 } pose)
                    continue;

                var newIndex = pose.Pose + 1;
                if (newIndex == cmd.PoseIndex)
                    continue;

                cmd.PoseIndex = newIndex;
                cmd.Name = $"{dozeName} Pose {newIndex}";
                changed++;
            }
        }

        if (changed > 0)
            log.Information($"Renumbered {changed} doze pose row(s) to the game's pose order.");
        configuration.Version = 2;
        configuration.Save();
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
            lastAppliedModsKey = null;
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

        StartTrace($"Use '{emote.Name}' row '{cmd.Name}' {cmd.Command} pose={cmd.PoseIndex} autoToggle={emote.AutoToggleMod} mods={emote.AssociatedMods.Count}");

        // An anim-locked preview would block the real emote
        StopPreview();
        pendingPose = null;
        StopTransitionSkip();

        var poseType = emoteData.GetPoseType(cmd.Command);
        // Seated/dozing in this command's own stance (standing idle is not a stance to keep)
        var inStance = poseType is not null and not 0 && GetStancePoseIndex(poseType.Value) != null;

        var redraw = false;
        // This use's mod settings when it plays modded, recorded as lastPlayed (null = vanilla)
        string? playedKey = null;
        if (emote.AutoToggleMod)
        {
            var newEmote = lastUsedEmote?.Id != emote.Id;
            var modsKey = ModSettingsKey(emote);
            var modsChanged = modsKey != lastAppliedModsKey;

            // Remove previous emote's temporary mod settings before applying new ones
            if (lastUsedEmote != null && newEmote)
                DisableEmoteMods(lastUsedEmote, skipRedraw: true);

            ApplyAssociatedMods(emote);

            lastUsedEmote = emote;
            lastAppliedModsKey = modsKey;
            var modded = AppliesAnyMod(emote);
            if (modded)
                playedKey = modsKey;

            // A redraw (the character briefly vanishes) reloads animations the character already
            // has loaded — only useful when the mod settings behind them changed: the emote played
            // right before used the same command with another mod, or another emote's mods replace
            // the stance animation playing right now. Replaying the same emote, or a pose change
            // within it, skips the redraw.
            redraw = configuration.AlwaysRedraw || (modded && FollowsSameCommandWithOtherMods(cmd.Command, modsKey)) ||
                     (inStance && modsChanged);
        }

        Trace($"Decision: poseType={poseType?.ToString() ?? "none"} inStance={inStance} redraw={redraw}");

        // Play on a later frame: after Penumbra has applied the settings, and after the redraw
        // has finished — an emote started underneath a redraw gets cancelled by it
        var now = DateTime.UtcNow;
        pendingPlay = new PendingPlay
        {
            Emote = emote,
            Command = cmd,
            WaitingForRedraw = redraw && penumbraService.RedrawSelf(),
            NotBefore = now,
            Deadline = now + RedrawTimeout,
            ModsKey = playedKey,
        };
    }

    // An emote waiting for its turn to play, see UseEmote
    private PendingPlay? pendingPlay;

    private sealed class PendingPlay
    {
        public required EmoteEntry Emote;
        public required EmoteCommandEntry Command;
        public bool WaitingForRedraw;
        public DateTime NotBefore;
        // Play anyway when the redraw-finished event never arrives
        public DateTime Deadline;
        // The mod settings it plays with when modded (null = vanilla), recorded once played
        public string? ModsKey;
    }

    // Let the freshly redrawn character settle before the emote starts
    private static readonly TimeSpan RedrawSettle = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RedrawTimeout = TimeSpan.FromSeconds(3);

    private void OnLocalPlayerRedrawn()
    {
        // Every animation reloads with the current settings
        lastPlayed = null;

        if (pendingPlay is not { WaitingForRedraw: true } play)
            return;

        play.WaitingForRedraw = false;
        play.NotBefore = DateTime.UtcNow + RedrawSettle;
        Trace("Penumbra reports redraw finished.");
    }

    private void PlayEmote(EmoteEntry emote, EmoteCommandEntry cmd)
    {
        var poseType = emoteData.GetPoseType(cmd.Command);

        // Standing idle pose rows never send /changepose raw: it IS the cycle command,
        // and sending it would move the pose before the watcher corrects it
        if (poseType == 0)
        {
            if (cmd.PoseIndex > 0)
            {
                QueuePoseChange(0, (byte)(cmd.PoseIndex - 1));
                log.Information($"Emote {emote.Name}: idle pose {cmd.PoseIndex} queued — applies when standing (5s timeout).");
            }
            else
            {
                PlayCommand(emote, cmd);
            }
            return;
        }

        if (poseType == null)
        {
            PlayCommand(emote, cmd);
            return;
        }

        // A plain /doze row is the standing doze emote, not one of the lying doze poses
        if (poseType == 4 && cmd.PoseIndex == 0)
        {
            PlayCommand(emote, cmd);
            return;
        }

        // Sit/groundsit/doze. The game enters the stance in the pose it remembers (the last
        // one used), so a row without a pose number means pose 1 — the base animation mods replace
        var stance = poseType.Value;
        var targetIndex = (byte)(Math.Max(cmd.PoseIndex, 1) - 1);

        // Already in this stance (checked after any redraw, which reloaded the animation)
        var currentIndex = GetStancePoseIndex(stance);
        if (currentIndex != null)
        {
            if (currentIndex == targetIndex)
            {
                log.Information($"Used emote: {emote.Name} (already in pose {targetIndex + 1})");
                return;
            }

            // Only /cpose changes the pose the character is in: a hidden pose emote merely plays
            // that pose's transition and falls back to the current pose. The watcher primes the
            // remembered pose so a single /cpose lands on the target.
            QueuePoseChange(stance, targetIndex, alreadyInStance: true);
            StartTransitionSkip(stance, targetIndex);
            log.Information($"Used emote: {emote.Name} (already in stance, /cpose to pose {targetIndex + 1})");
            return;
        }

        EnterStance(emote, cmd, stance, targetIndex);
    }

    /// <summary> Enter a sit/groundsit/doze stance directly in the target pose. </summary>
    private void EnterStance(EmoteEntry emote, EmoteCommandEntry cmd, byte stance, byte targetIndex)
    {
        SetRememberedPose(stance, targetIndex);

        // With Sit/Doze Anywhere, play the hidden emote of exactly this pose. The generic
        // anywhere emote is a fixed pose (e.g. 99 = doze pose 3): the game lies down, then
        // switches into that pose a few seconds later, away from the pose the mod replaces.
        var poseEmote = configuration.SitDozeAnywhere ? emoteData.GetPoseEmoteId(stance, targetIndex) : null;
        if (poseEmote != null)
        {
            ExecuteHotbarEmote(poseEmote.Value);
            Trace($"Executed pose emote {poseEmote.Value} (pose {targetIndex + 1}).");
            log.Information($"Used emote: {emote.Name} (pose {targetIndex + 1} via emote {poseEmote.Value})");
        }
        else
        {
            PlayCommand(emote, cmd);
        }

        // /cpose once seated if the game lands elsewhere — and, when skipping transitions, to cut
        // lying awake short: dozing via a pose emote lies awake ~5s before falling asleep into
        // the remembered pose, while a /cpose sends it there right away
        if (configuration.AutoPoseCorrection || configuration.SkipPoseTransitions)
            QueuePoseChange(stance, targetIndex);

        StartTransitionSkip(stance, targetIndex);
    }

    // Local-only replacement of a stance's pose-change transitions with the target pose's loop
    private TransitionSkip? transitionSkip;

    private sealed class TransitionSkip
    {
        public required byte Stance;
        public required byte TargetIndex;
        public required ushort TargetLoop;
        public required string TargetLoopKey;
        public required uint ModeRow;
        // The stance's mode must be entered by then, or skipping is abandoned
        public DateTime EnterDeadline;
        public bool Entered;
        // Slot 0 timeline and emote seen last tick: swap once per started transition
        public (ushort Timeline, ushort Emote) LastSeen;
    }

    private static readonly TimeSpan TransitionSkipEnterTimeout = TimeSpan.FromSeconds(4);

    private void StartTransitionSkip(byte stance, byte targetIndex)
    {
        StopTransitionSkip();
        if (!configuration.SkipPoseTransitions)
            return;

        var loop = emoteData.GetPoseLoopTimeline(stance, targetIndex);
        var modeRow = emoteData.GetStanceModeRow(stance);
        if (loop == null || modeRow == null)
            return;

        transitionSkip = new TransitionSkip
        {
            Stance = stance,
            TargetIndex = targetIndex,
            TargetLoop = loop.Value.Id,
            TargetLoopKey = loop.Value.Key,
            ModeRow = modeRow.Value,
            EnterDeadline = DateTime.UtcNow + TransitionSkipEnterTimeout,
        };
    }

    private void StopTransitionSkip(string? reason = null)
    {
        if (transitionSkip == null)
            return;

        transitionSkip = null;
        if (reason != null)
            Trace($"Transition skip stopped: {reason}.");
    }

    /// <summary>
    /// Per tick while a sit/groundsit/doze pose is being entered or held: when the game starts a
    /// pose-change transition (e.g. emote/l_pose01_start), play the target pose's loop instead —
    /// a one-off client-side timeline swap like the preview, so other players are unaffected.
    /// Lying awake is not swapped (the game re-asserts it every frame); the /cpose watcher ends
    /// it instead. Stops when the stance is left, or when the player changes the pose themselves.
    /// </summary>
    private unsafe void WatchTransitionSkip()
    {
        var skip = transitionSkip;
        if (skip == null)
            return;

        var chara = GetLocalCharacter();
        if (chara == null)
        {
            transitionSkip = null;
            return;
        }

        var inMode = chara->Mode == FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.InPositionLoop &&
                     chara->ModeParam == skip.ModeRow;
        if (!inMode)
        {
            if (skip.Entered || DateTime.UtcNow > skip.EnterDeadline)
                StopTransitionSkip("stance left");
            return;
        }

        skip.Entered = true;

        // A manual /cpose to another pose: stop forcing the old one
        var ec = &chara->EmoteController;
        if (pendingPose == null && (byte)ec->CurrentPoseType == skip.Stance && ec->CPoseState != skip.TargetIndex)
        {
            StopTransitionSkip($"pose changed to {ec->CPoseState + 1} by the player");
            return;
        }

        var current = chara->Timeline.TimelineSequencer.TimelineIds[0];
        var seen = (current, ec->EmoteId);
        var isNew = seen != skip.LastSeen;
        skip.LastSeen = seen;
        if (!isNew || current == 0 || current == skip.TargetLoop)
            return;

        // Only pose changes within the stance: the entry (e.g. the hidden pose emote lying down
        // from standing) plays normally — swapping it would flash the loop for a frame before
        // the game's lying-awake loop takes over
        if ((byte)ec->CurrentPoseType != skip.Stance)
            return;

        var key = emoteData.GetStanceTransitionKey(skip.Stance, current);
        if (key == null)
            return;

        chara->Timeline.TimelineSequencer.PlayTimeline(skip.TargetLoop, null);
        Trace($"Transition skip: {key} -> {skip.TargetLoopKey}");
    }

    private static string NormalizeChatCommand(string command)
        => command.StartsWith('/') ? command : "/" + command;

    private void PlayCommand(EmoteEntry emote, EmoteCommandEntry cmd)
    {
        // With Sit/Doze Anywhere enabled, execute the game's hidden furniture emote
        // instead of the chat command, so sit/doze work without a chair or bed nearby.
        var anywhereId = configuration.SitDozeAnywhere ? emoteData.GetAnywhereEmoteId(cmd.Command) : null;
        if (anywhereId != null)
        {
            ExecuteHotbarEmote(anywhereId.Value);
            Trace($"Executed hidden anywhere emote {anywhereId.Value}.");
            log.Information($"Used emote: {emote.Name} (anywhere emote {anywhereId.Value})");
        }
        else
        {
            // Send the emote command via chat
            var command = NormalizeChatCommand(cmd.Command);
            SendChatCommand(command);
            log.Information($"Used emote: {emote.Name} ({command})");
        }
    }

    /// <summary>
    /// Set the pose the game remembers for a stance (PlayerState.SelectedPoses, what /cpose
    /// advances) — the next sit/groundsit/doze enters that pose directly.
    /// </summary>
    private unsafe void SetRememberedPose(byte poseType, byte index)
    {
        var playerState = PlayerState.Instance();
        if (playerState == null)
            return;

        var maxIndex = (int)EmoteController.GetAvailablePoses((EmoteController.PoseType)poseType);
        var value = (byte)Math.Min(index, maxIndex);
        var previous = playerState->SelectedPoses[poseType];
        playerState->SelectedPoses[poseType] = value;
        if (previous != value)
            log.Debug($"Remembered pose for type {poseType}: {previous + 1} -> {value + 1}.");
        Trace($"Set remembered pose type {poseType}: {previous + 1} -> {value + 1} (now reads {playerState->SelectedPoses[poseType] + 1}).");
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

        // A queued emote or pose change must not fire into the anim lock
        pendingPlay = null;
        pendingPose = null;
        // Before the preview saves the base override — it must not capture ours
        StopTransitionSkip();

        // Track preview-applied mods exactly like a real use, so the next
        // emote's cleanup pass removes them
        if (emote.AutoToggleMod)
        {
            if (lastUsedEmote != null && lastUsedEmote.Id != emote.Id)
                DisableEmoteMods(lastUsedEmote, skipRedraw: true);

            ApplyAssociatedMods(emote);
            lastUsedEmote = emote;
            lastAppliedModsKey = ModSettingsKey(emote);
        }

        // The preview loads this command's animation with those settings, like a real play
        RecordPlayed(cmd.Command, emote.AutoToggleMod && AppliesAnyMod(emote) ? ModSettingsKey(emote) : null);

        // Keep the FIRST original state when chaining previews
        previewOriginalState ??= (chara->Mode, chara->ModeParam, chara->Timeline.BaseOverride);
        previewStartPosition = localPlayer.Position;

        chara->SetMode(FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.AnimLock, 0);
        chara->Timeline.BaseOverride = timelineId.Value;
        chara->Timeline.TimelineSequencer.PlayTimeline(timelineId.Value, null);

        log.Information($"Previewing emote: {emote.Name} (timeline {timelineId.Value}, local only)");
        return true;
    }

    /// <summary> Whether using this emote turns any mod on (Auto-apply with an enabled association). </summary>
    private static bool AppliesAnyMod(EmoteEntry emote)
        => emote.AssociatedMods.Any(m => m.Enabled);

    /// <summary>
    /// Whether the emote played right before used this command modded with other mod settings —
    /// that modded animation is still loaded and the new one would collide with it.
    /// </summary>
    private bool FollowsSameCommandWithOtherMods(string command, string modsKey)
    {
        SyncLastPlayedWithDrawObject();
        return lastPlayed is { ModsKey: { } previous } played &&
               played.Command.Equals(command.TrimStart('/'), StringComparison.OrdinalIgnoreCase) &&
               previous != modsKey;
    }

    private void RecordPlayed(string command, string? modsKey)
    {
        SyncLastPlayedWithDrawObject();
        lastPlayed = (command.TrimStart('/'), modsKey);
    }

    /// <summary> A new draw object (zoning, a redraw not via Penumbra) has nothing loaded yet. </summary>
    private unsafe void SyncLastPlayedWithDrawObject()
    {
        var chara = GetLocalCharacter();
        var drawObject = chara == null ? nint.Zero : (nint)chara->DrawObject;
        if (drawObject == lastPlayedDrawObject)
            return;

        lastPlayed = null;
        lastPlayedDrawObject = drawObject;
    }

    /// <summary> The mod settings an emote applies, as a comparable string. </summary>
    private static string ModSettingsKey(EmoteEntry emote)
        => $"{emote.PenumbraCollectionId}|" + string.Join(";", emote.AssociatedMods.Select(m =>
            $"{m.ModDirectory}:{m.Enabled}:{m.Inherit}:{m.Priority}:" +
            string.Join(",", (m.Settings ?? new()).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={string.Join("+", kv.Value)}"))));

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

    // Diagnostic trace (Configuration.TraceEmotes): state changes for a while after an emote use
    private static readonly TimeSpan TraceDuration = TimeSpan.FromSeconds(20); // dozing falls asleep after ~9s
    private DateTime traceStart;
    private DateTime traceUntil;
    private string? lastTraceState;
    private readonly HashSet<ushort> tracedTimelines = new();
    // Body animation playback (Havok controls) is sampled, not change-logged: its time moves every frame
    private static readonly TimeSpan AnimTraceInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AnimTraceDuration = TimeSpan.FromSeconds(10);
    private DateTime nextAnimTrace;

    private void StartTrace(string message)
    {
        if (!configuration.TraceEmotes)
            return;

        traceStart = DateTime.UtcNow;
        traceUntil = traceStart + TraceDuration;
        lastTraceState = null;
        tracedTimelines.Clear();
        nextAnimTrace = default;
        Trace(message);
    }

    private void Trace(string message)
    {
        if (DateTime.UtcNow > traceUntil)
            return;

        log.Information($"[Trace +{(DateTime.UtcNow - traceStart).TotalMilliseconds:0}ms] {message}");
    }

    /// <summary> Per tick while tracing: log the character state whenever it changes. </summary>
    private unsafe void WatchTrace()
    {
        if (traceUntil == default)
            return;

        if (DateTime.UtcNow > traceUntil)
        {
            traceUntil = default;
            log.Information("[Trace] end");
            return;
        }

        var chara = GetLocalCharacter();
        if (chara == null)
            return;

        var ec = &chara->EmoteController;
        var playerState = PlayerState.Instance();
        var remembered = playerState == null
            ? "?"
            : $"sit {playerState->SelectedPoses[2] + 1}, ground {playerState->SelectedPoses[3] + 1}, doze {playerState->SelectedPoses[4] + 1}";

        var timelines = new List<string>();
        var ids = chara->Timeline.TimelineSequencer.TimelineIds;
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] != 0)
                timelines.Add($"{i}:{ids[i]} {emoteData.GetTimelineKey(ids[i]) ?? "?"}");
        }

        var state = $"stance={ec->CurrentPoseType} cpose={ec->CPoseState + 1} remembered=[{remembered}] " +
                    $"mode={chara->Mode}/{chara->ModeParam} emote={ec->EmoteId} loop={ec->IsInEmoteLoop()} " +
                    $"baseOverride={chara->Timeline.BaseOverride} timelines=[{string.Join(", ", timelines)}]";
        if (state != lastTraceState)
        {
            lastTraceState = state;
            Trace(state);
        }

        // Which file Penumbra loads for each newly seen animation — shows whether the mod applies
        var race = GetLocalRaceCode(chara);
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] == 0 || !tracedTimelines.Add(ids[i]))
                continue;

            var key = emoteData.GetTimelineKey(ids[i]);
            if (key == null || race == null)
                continue;

            var gamePath = $"chara/human/c{race.Value:0000}/animation/a0001/bt_common/{key}.pap";
            var resolved = penumbraService.ResolvePlayerPath(gamePath);
            Trace($"File for {key}: {(resolved == null ? "(Penumbra unavailable)" : resolved.Equals(gamePath, StringComparison.OrdinalIgnoreCase) ? "vanilla " + gamePath : resolved)}");
        }

        TraceAnimationControls(chara);
    }

    /// <summary>
    /// Sample the body skeleton's Havok animation controls: which clips play (by length), where in
    /// each clip, and their blend weight — shows the clip actually loaded (e.g. an edited .pap) and
    /// when it starts moving relative to the command.
    /// </summary>
    private unsafe void TraceAnimationControls(FFXIVClientStructs.FFXIV.Client.Game.Character.Character* chara)
    {
        if (!configuration.TraceAnimation)
            return;

        var now = DateTime.UtcNow;
        if (now < nextAnimTrace || now > traceStart + AnimTraceDuration)
            return;
        nextAnimTrace = now + AnimTraceInterval;

        var drawObject = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)chara->DrawObject;
        if (drawObject == null || drawObject->Skeleton == null || drawObject->Skeleton->PartialSkeletonCount == 0)
            return;

        var animated = drawObject->Skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if (animated == null)
            return;

        var controls = new List<string>();
        for (var i = 0; i < animated->AnimationControls.Length; i++)
        {
            var control = animated->AnimationControls[i].Value;
            if (control == null)
                continue;

            var binding = control->hkaAnimationControl.Binding.ptr;
            var animation = binding == null ? null : binding->Animation.ptr;
            var duration = animation == null ? 0f : animation->Duration;
            controls.Add($"{i}: {control->hkaAnimationControl.LocalTime:0.00}/{duration:0.00}s " +
                         $"w={control->hkaAnimationControl.Weight * control->MasterWeight:0.00} x{control->PlaybackSpeed:0.##}");
        }

        Trace($"Anim: {string.Join(" | ", controls)}");
    }

    /// <summary> The local character's race/sex model code (e.g. 101 = c0101), or null. </summary>
    private static unsafe ushort? GetLocalRaceCode(FFXIVClientStructs.FFXIV.Client.Game.Character.Character* chara)
    {
        var drawObject = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)chara->DrawObject;
        if (drawObject == null ||
            drawObject->GetModelType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType.Human)
            return null;

        return ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Human*)drawObject)->RaceSexId;
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

    private void QueuePoseChange(byte poseType, byte targetIndex, bool alreadyInStance = false)
        => pendingPose = new PendingPose
        {
            PoseType = poseType,
            TargetIndex = targetIndex,
            Deadline = DateTime.UtcNow.AddSeconds(5),
            // Already settled in the stance — no need to wait for a sit-down to finish
            InStanceSince = alreadyInStance ? DateTime.UtcNow - PoseStanceSettle : null,
        };

    /// <summary>
    /// The current 0-based pose index when the local player is in this sit/groundsit/doze
    /// stance, or null when not. Lying in bed awake (doze pose 1) is the bed mode without the
    /// Doze pose type, so the stance's emote mode counts too.
    /// </summary>
    private unsafe byte? GetStancePoseIndex(byte stance)
    {
        var chara = GetLocalCharacter();
        if (chara == null)
            return null;

        if ((byte)chara->EmoteController.CurrentPoseType == stance)
            return chara->EmoteController.CPoseState;

        var modeRow = emoteData.GetStanceModeRow(stance);
        if (modeRow != null &&
            chara->Mode == FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.InPositionLoop &&
            chara->ModeParam == modeRow)
            return 0;

        return null;
    }

    /// <summary>
    /// The current standing idle pose (0-based) when standing normally, or null. The pose type
    /// only reads Idle (0) after a /cpose — standing before that reports none (255) — so the
    /// remembered idle pose stands in for the current one then.
    /// </summary>
    private unsafe byte? GetIdlePoseIndex()
    {
        var chara = GetLocalCharacter();
        if (chara == null || chara->Mode != FFXIVClientStructs.FFXIV.Client.Game.Character.CharacterModes.Normal)
            return null;

        var poseType = (byte)chara->EmoteController.CurrentPoseType;
        if (poseType == 0)
            return chara->EmoteController.CPoseState;
        if (poseType != byte.MaxValue)
            return null; // seated, dozing, ...

        var playerState = PlayerState.Instance();
        return playerState == null ? null : playerState->SelectedPoses[0];
    }

    /// <summary>
    /// Whether the local player is in the stance's mode playing its own start emote's loop
    /// without having taken the stance's pose yet — lying in bed awake (emote 88) before dozing off.
    /// </summary>
    private unsafe bool IsInStanceStartLoop(byte stance)
    {
        var chara = GetLocalCharacter();
        if (chara == null || (byte)chara->EmoteController.CurrentPoseType == stance)
            return false;

        var startEmote = emoteData.GetStanceStartEmoteId(stance);
        return startEmote != null && chara->EmoteController.EmoteId == startEmote;
    }

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
    /// cycles /cpose until the requested pose is reached (the approach used by pose
    /// plugins — the game offers no direct set). One /cpose per step: the game drops
    /// /cpose while the sit-down or a pose transition is still playing, so each step
    /// waits for the pose index to change and resends when it didn't.
    /// </summary>
    private void OnFrameworkUpdate(IFramework _)
    {
        WatchPreview();
        WatchTrace();
        WatchTransitionSkip();

        var play = pendingPlay;
        if (play != null && (!play.WaitingForRedraw || DateTime.UtcNow > play.Deadline) && DateTime.UtcNow >= play.NotBefore)
        {
            pendingPlay = null;
            Trace(play.WaitingForRedraw ? "Playing (redraw event never arrived, timeout)." : "Playing.");
            PlayEmote(play.Emote, play.Command);
            RecordPlayed(play.Command.Command, play.ModsKey);
        }


        var pose = pendingPose;
        if (pose == null)
            return;

        var now = DateTime.UtcNow;
        if (now > pose.Deadline)
        {
            log.Debug($"Pose change to {pose.TargetIndex + 1} (type {pose.PoseType}) timed out.");
            Trace($"/cpose watcher timed out (target pose {pose.TargetIndex + 1}).");
            pendingPose = null;
            return;
        }

        // Sit/groundsit/doze count lying awake (bed mode, before dozing off) as pose 1
        byte? stanceIndex;
        var lyingAwake = false;
        if (pose.PoseType == 0)
        {
            stanceIndex = GetIdlePoseIndex();
        }
        else
        {
            stanceIndex = GetStancePoseIndex(pose.PoseType);
            lyingAwake = stanceIndex != null && IsInStanceStartLoop(pose.PoseType);
        }

        if (stanceIndex == null)
        {
            pose.InStanceSince = null;
            return; // stance not entered yet — keep waiting until the deadline
        }

        // The sit-down plays first and the pose index can lag behind the stance —
        // acting on it right away cycles away from a correctly entered pose
        pose.InStanceSince ??= now;
        if (pose.SentFromIndex == null && !lyingAwake && now - pose.InStanceSince < PoseStanceSettle)
            return;

        // Per Poser: GetAvailablePoses returns the highest valid pose index
        var totalPoses = EmoteController.GetAvailablePoses((EmoteController.PoseType)pose.PoseType) + 1;
        var current = stanceIndex.Value;
        var target = Math.Min(pose.TargetIndex, totalPoses - 1);

        if (current == target)
        {
            if (pose.SentFromIndex != null)
                log.Debug($"Reached pose {target + 1} (type {pose.PoseType}).");
            Trace($"/cpose watcher: in pose {target + 1}.");
            pendingPose = null;
            return;
        }

        // Previous step still pending — give it time to land before resending
        if (pose.SentFromIndex == current && now - pose.SentAt < (lyingAwake ? PoseStepRetryLyingAwake : PoseStepRetry))
            return;

        // Extend the deadline on the first step and on progress, never on a resend,
        // so a /cpose the game keeps refusing can't retry forever
        if (pose.SentFromIndex != current)
            pose.Deadline = now.AddSeconds(4);

        // /cpose advances from the REMEMBERED pose, not the current one: prime it with the pose
        // before the target so this single /cpose lands on the target directly
        SetRememberedPose(pose.PoseType, (byte)((target + totalPoses - 1) % totalPoses));
        SendChatCommand("/cpose");
        pose.SentFromIndex = current;
        pose.SentAt = now;
    }

    private unsafe void SendChatCommand(string command)
    {
        Trace($"Send chat command {command}");
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
        lastAppliedModsKey = null;
        penumbraService.RedrawSelf();
    }
}

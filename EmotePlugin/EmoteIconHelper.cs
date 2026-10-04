using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace EmotePlugin;

public class EmoteIconHelper
{
    private const uint ExpressionsCategoryRowId = 3;
    private const uint DozeEmoteRowId = 13;
    private const uint ChairSitEmoteRowId = 50;
    private const uint ChangePoseEmoteRowId = 90;
    private const uint GroundSitEmoteModeRowId = 1;
    private const uint ChairSitEmoteModeRowId = 2;
    private const uint BedEmoteModeRowId = 3; // lying in bed: doze poses

    // Hidden emote ids the game uses internally for furniture interactions —
    // executing them plays sit/doze without requiring a chair/bed.
    private const uint SitAnywhereEmoteId = 96;
    private const uint DozeAnywhereEmoteId = 99;

    private readonly Dictionary<string, uint> iconCache = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> nameToCommand = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expressionCommands = new(System.StringComparer.OrdinalIgnoreCase);
    // Command (any variant) -> EmoteController.PoseType value for pose-mode emotes (sit/groundsit/doze)
    private readonly Dictionary<string, byte> poseTypeCommands = new(System.StringComparer.OrdinalIgnoreCase);
    // Command (any variant) -> hidden "anywhere" emote id (chair sit and doze only)
    private readonly Dictionary<string, uint> anywhereCommands = new(System.StringComparer.OrdinalIgnoreCase);
    // EmoteController.PoseType value -> (main command, emote name), for building rows from pose files
    private readonly Dictionary<byte, (string Command, string Name)> poseTypeInfo = new();
    // Command (any variant) -> the emote's primary ActionTimeline id, for local previews
    private readonly Dictionary<string, ushort> commandTimelines = new(System.StringComparer.OrdinalIgnoreCase);
    // ActionTimeline key (e.g. "emote/l_pose02_loop") -> row id, for pose-variant previews
    private readonly Dictionary<string, ushort> poseLoopTimelines = new(System.StringComparer.OrdinalIgnoreCase);
    // EmoteController.PoseType -> the game's pose table for that stance, by 0-based pose index
    private readonly Dictionary<byte, List<StancePose>> stancePoses = new();
    // Stance animation key (e.g. "emote/l_pose01_loop") -> (stance, 0-based pose index)
    private readonly Dictionary<string, (byte PoseType, byte Pose)> stancePoseKeys = new(System.StringComparer.OrdinalIgnoreCase);

    private readonly record struct StancePose(uint EmoteId, bool HasTextCommand, ushort LoopTimeline, string? LoopKey);

    // Stance -> every ActionTimeline row of its pose-change transitions (the intros of poses 2+,
    // e.g. emote/l_pose01_start) -> key. Several rows can share one key.
    private readonly Dictionary<byte, Dictionary<ushort, string>> stanceTransitions = new();

    // EmoteMode row of each stance -> EmoteController.PoseType
    private static readonly Dictionary<uint, byte> StanceModes = new()
    {
        [ChairSitEmoteModeRowId] = 2,
        [GroundSitEmoteModeRowId] = 3,
        [BedEmoteModeRowId] = 4,
    };

    // Standing idle pose files: emote/pose0N = /changepose pose index N
    private static readonly Regex IdlePoseKeyRegex = new(@"^emote/pose(\d{2})_(?:loop|start)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ITextureProvider textureProvider;
    private readonly IDataManager dataManager;

    public EmoteIconHelper(ITextureProvider textureProvider, IDataManager dataManager)
    {
        this.textureProvider = textureProvider;
        this.dataManager = dataManager;
        BuildCache(dataManager);
    }

    private void BuildCache(IDataManager dataManager)
    {
        // Pose-variant loop timelines (emote/pose01_loop, emote/l_pose02_loop, ...)
        var timelineSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ActionTimeline>();
        if (timelineSheet != null)
        {
            foreach (var timeline in timelineSheet)
            {
                var key = timeline.Key.ExtractText();
                if (key.StartsWith("emote/", System.StringComparison.Ordinal) &&
                    key.EndsWith("_loop", System.StringComparison.Ordinal) &&
                    key.Contains("pose", System.StringComparison.Ordinal))
                    poseLoopTimelines.TryAdd(key, (ushort)timeline.RowId);
            }
        }

        var emoteSheet = dataManager.GetExcelSheet<Emote>();
        if (emoteSheet == null) return;

        BuildPoseTables(emoteSheet);

        foreach (var emote in emoteSheet)
        {
            if (emote.Icon == 0) continue;
            var textCommand = emote.TextCommand.ValueNullable;
            if (textCommand == null) continue;

            // EmoteController.PoseType values: Idle = 0, Sit = 2, GroundSit = 3, Doze = 4.
            // Chair sit (/lounge, alias /sit) and ground sit are flagged via EmoteMode;
            // Doze and Change Pose (/changepose, alias /cpose → standing idle poses)
            // have no EmoteMode in the sheet and are matched by their row ids.
            byte? poseType = emote.RowId == DozeEmoteRowId ? (byte)4
                : emote.RowId == ChangePoseEmoteRowId ? (byte)0
                : emote.EmoteMode.RowId == ChairSitEmoteModeRowId ? (byte)2
                : emote.EmoteMode.RowId == GroundSitEmoteModeRowId ? (byte)3
                : null;

            uint? anywhereId = emote.RowId == DozeEmoteRowId ? DozeAnywhereEmoteId
                : emote.RowId == ChairSitEmoteRowId ? SitAnywhereEmoteId
                : null;

            var cmd = textCommand.Value.Command.ExtractText();
            if (!string.IsNullOrEmpty(cmd))
            {
                iconCache.TryAdd(cmd, emote.Icon);

                var emoteName = emote.Name.ExtractText();
                if (!string.IsNullOrEmpty(emoteName))
                    nameToCommand.TryAdd(emoteName, cmd);

                if (emote.EmoteCategory.RowId == ExpressionsCategoryRowId)
                    expressionCommands.Add(cmd);

                if (poseType != null)
                    poseTypeInfo.TryAdd(poseType.Value, (cmd, emoteName));
            }

            var alias = textCommand.Value.Alias.ExtractText();
            var shortCmd = textCommand.Value.ShortCommand.ExtractText();
            var shortAlias = textCommand.Value.ShortAlias.ExtractText();

            var timelineId = emote.ActionTimeline.Count > 0 ? emote.ActionTimeline[0].RowId : 0;

            foreach (var variant in new[] { cmd, alias, shortCmd, shortAlias })
            {
                if (string.IsNullOrEmpty(variant))
                    continue;

                iconCache.TryAdd(variant, emote.Icon);
                if (poseType != null)
                    poseTypeCommands.TryAdd(variant, poseType.Value);
                if (anywhereId != null)
                    anywhereCommands.TryAdd(variant, anywhereId.Value);
                if (timelineId != 0)
                    commandTimelines.TryAdd(variant, (ushort)timelineId);
            }
        }
    }

    /// <summary>
    /// Build each stance's pose table the way the game orders it: the mode's start emote is
    /// pose index 0, then the mode's hidden furniture emotes by row id (the end emote excluded).
    /// File names do NOT give the order — doze is 88 bed_liedown (awake), 99 l_pose02,
    /// 100 l_pose01, confirmed by traces: remembered doze index 1 falls asleep into 99.
    /// Sit (50 sit, 95..255 s_pose01..04) and ground sit (52 jmn, 97..117 j_pose01..03)
    /// happen to match their file numbers.
    /// </summary>
    private void BuildPoseTables(Lumina.Excel.ExcelSheet<Emote> emoteSheet)
    {
        var modeSheet = dataManager.GetExcelSheet<EmoteMode>();
        if (modeSheet == null)
            return;

        var transitionKeys = new Dictionary<string, byte>(System.StringComparer.OrdinalIgnoreCase);

        foreach (var (modeId, poseType) in StanceModes)
        {
            if (modeSheet.GetRowOrDefault(modeId) is not { } mode)
                continue;

            var startId = mode.StartEmote.RowId;
            var endId = mode.EndEmote.RowId;
            var rows = new List<Emote>();
            if (emoteSheet.GetRowOrDefault(startId) is { } start)
                rows.Add(start);
            rows.AddRange(emoteSheet
                .Where(e => e.EmoteMode.RowId == modeId && e.RowId != startId && e.RowId != endId)
                .OrderBy(e => e.RowId));

            var poses = new List<StancePose>();
            foreach (var emote in rows)
            {
                var index = (byte)poses.Count;
                var loop = emote.ActionTimeline.Count > 0 ? emote.ActionTimeline[0].ValueNullable : null;
                var intro = emote.ActionTimeline.Count > 1 ? emote.ActionTimeline[1].ValueNullable : null;
                var loopKey = loop?.Key.ExtractText();
                poses.Add(new StancePose(emote.RowId, emote.TextCommand.RowId != 0, (ushort)(loop?.RowId ?? 0), loopKey));

                // Only stance-specific files identify a pose — the chair/ground intros are
                // shared event_base files (the sit-down itself)
                var introKey = intro?.Key.ExtractText();
                foreach (var key in new[] { loopKey, introKey })
                {
                    if (!string.IsNullOrEmpty(key) && key.StartsWith("emote/", System.StringComparison.OrdinalIgnoreCase))
                        stancePoseKeys.TryAdd(key, (poseType, index));
                }

                // The intro of pose 2+ is the pose-change transition; pose 1's is the sit-down /
                // lie-down itself (kept)
                if (index > 0 && !string.IsNullOrEmpty(introKey) &&
                    introKey.StartsWith("emote/", System.StringComparison.OrdinalIgnoreCase))
                    transitionKeys.TryAdd(introKey, poseType);
            }

            stancePoses[poseType] = poses;
        }

        var timelineSheet = dataManager.GetExcelSheet<ActionTimeline>();
        if (timelineSheet == null)
            return;

        foreach (var timeline in timelineSheet)
        {
            var key = timeline.Key.ExtractText();
            if (!transitionKeys.TryGetValue(key, out var poseType))
                continue;

            if (!stanceTransitions.TryGetValue(poseType, out var ids))
                stanceTransitions[poseType] = ids = new Dictionary<ushort, string>();
            ids[(ushort)timeline.RowId] = key;
        }
    }

    /// <summary> The loop timeline (row id and key) of a sit/groundsit/doze pose (0-based), or null. </summary>
    public (ushort Id, string Key)? GetPoseLoopTimeline(byte poseType, byte poseIndex)
        => stancePoses.TryGetValue(poseType, out var poses) && poseIndex < poses.Count &&
           poses[poseIndex].LoopTimeline != 0 && poses[poseIndex].LoopKey != null
            ? (poses[poseIndex].LoopTimeline, poses[poseIndex].LoopKey!)
            : null;

    /// <summary>
    /// The key of a timeline when it is one of this stance's pose-change transitions
    /// (e.g. emote/l_pose01_start), or null — the sit-down, lie-down and stand-up are not.
    /// </summary>
    public string? GetStanceTransitionKey(byte poseType, ushort timelineId)
        => stanceTransitions.TryGetValue(poseType, out var ids) && ids.TryGetValue(timelineId, out var key) ? key : null;

    /// <summary>
    /// The pose an animation file plays, from its timeline key (e.g. "emote/l_pose01_loop"):
    /// (EmoteController.PoseType, 0-based pose index), or null when it is not a pose animation.
    /// </summary>
    public (byte PoseType, byte Pose)? GetPoseForAnimationKey(string key)
    {
        if (stancePoseKeys.TryGetValue(key, out var pose))
            return pose;

        var idle = IdlePoseKeyRegex.Match(key);
        if (idle.Success && byte.TryParse(idle.Groups[1].Value, out var idleIndex))
            return (0, idleIndex);

        return null;
    }

    /// <summary>
    /// The hidden emote that plays exactly this sit/groundsit/doze pose (0-based), or null when
    /// there is none — or when it is a regular command (/sit needs a real chair; /groundsit is
    /// played as its command anyway).
    /// </summary>
    public uint? GetPoseEmoteId(byte poseType, byte poseIndex)
        => stancePoses.TryGetValue(poseType, out var poses) && poseIndex < poses.Count && !poses[poseIndex].HasTextCommand
            ? poses[poseIndex].EmoteId
            : null;

    /// <summary> The stance's own start emote (pose 1, e.g. 88 lying in bed awake), or null. </summary>
    public uint? GetStanceStartEmoteId(byte poseType)
        => stancePoses.TryGetValue(poseType, out var poses) && poses.Count > 0 ? poses[0].EmoteId : null;

    /// <summary> The EmoteMode row of a sit/groundsit/doze stance, or null. </summary>
    public uint? GetStanceModeRow(byte poseType)
    {
        foreach (var (modeId, type) in StanceModes)
        {
            if (type == poseType)
                return modeId;
        }
        return null;
    }

    /// <summary>
    /// The EmoteController.PoseType this command selects a pose for (sit/groundsit/doze),
    /// or null when the command is not a pose-mode emote.
    /// </summary>
    public byte? GetPoseType(string command)
    {
        var cmd = NormalizeCommand(command);
        return cmd != null && poseTypeCommands.TryGetValue(cmd, out var poseType) ? poseType : null;
    }

    /// <summary> The main command and emote name for a pose type (sit/groundsit/doze). </summary>
    public (string Command, string Name)? GetPoseTypeInfo(byte poseType)
        => poseTypeInfo.TryGetValue(poseType, out var info) ? info : null;

    /// <summary>
    /// The hidden "anywhere" emote id for this command (chair sit / doze),
    /// or null when the command has no anywhere variant.
    /// </summary>
    public uint? GetAnywhereEmoteId(string command)
    {
        var cmd = NormalizeCommand(command);
        return cmd != null && anywhereCommands.TryGetValue(cmd, out var emoteId) ? emoteId : null;
    }

    /// <summary>
    /// The ActionTimeline id to play for a local preview of this command row,
    /// or null when no timeline can be resolved.
    /// </summary>
    public ushort? GetPreviewTimeline(string command, int poseIndex)
    {
        var cmd = NormalizeCommand(command);
        if (cmd == null)
            return null;

        if (poseTypeCommands.TryGetValue(cmd, out var poseType))
        {
            // Stance poses: the loop of that pose's emote in the game's pose table
            if (poseIndex >= 1 && stancePoses.TryGetValue(poseType, out var poses))
            {
                return poseIndex - 1 < poses.Count && poses[poseIndex - 1].LoopTimeline != 0
                    ? poses[poseIndex - 1].LoopTimeline
                    : null;
            }

            // Standing idle variants 2+ map to their pose loop file; pose 1 is plain idle
            if (poseType == 0 && poseIndex >= 2)
            {
                return poseLoopTimelines.TryGetValue($"emote/pose{poseIndex - 1:00}_loop", out var poseTimeline)
                    ? poseTimeline
                    : null;
            }

            // Standing idle pose 1 is the true idle stance — it has no emote timeline;
            // /changepose's own timeline is the transition flourish, not a pose
            if (poseType == 0)
                return null;
        }

        return commandTimelines.TryGetValue(cmd, out var timeline) ? timeline : null;
    }

    /// <summary> The ActionTimeline key (e.g. "emote/doze") of a timeline row, or null. </summary>
    public string? GetTimelineKey(ushort timelineId)
    {
        var row = dataManager.GetExcelSheet<ActionTimeline>()?.GetRowOrDefault(timelineId);
        var key = row?.Key.ExtractText();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    private static string? NormalizeCommand(string command)
    {
        var cmd = command.Trim();
        if (string.IsNullOrEmpty(cmd))
            return null;
        return cmd.StartsWith('/') ? cmd : "/" + cmd;
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

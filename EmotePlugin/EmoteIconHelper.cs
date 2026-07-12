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
    private const uint DozeEmoteRowId = 13;
    private const uint ChairSitEmoteRowId = 50;
    private const uint ChangePoseEmoteRowId = 90;
    private const uint GroundSitEmoteModeRowId = 1;
    private const uint ChairSitEmoteModeRowId = 2;

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
    private readonly ITextureProvider textureProvider;

    public EmoteIconHelper(ITextureProvider textureProvider, IDataManager dataManager)
    {
        this.textureProvider = textureProvider;
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
            // Pose variants 2+ map to their pose loop file; pose 1 is the base animation
            if (poseIndex >= 2)
            {
                var prefix = poseType switch
                {
                    0 => "",   // standing idle
                    2 => "s_", // chair sit
                    3 => "j_", // ground sit
                    4 => "l_", // doze
                    _ => null,
                };
                if (prefix == null)
                    return null;

                return poseLoopTimelines.TryGetValue($"emote/{prefix}pose{poseIndex - 1:00}_loop", out var poseTimeline)
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

using System;

namespace ImmersiveWoodworking;

internal enum DebarkerPartGroup { Static, Spindle, Disc, Knives, Drive, Feed, Workpiece }

internal sealed record DebarkerPartRule(string Id, DebarkerPartGroup Group, params string[] Stems);

/// <summary>
/// The rule table that splits the debarker model by element name (AutoDebarkerPlan T07.02). The rules come from
/// <c>AutoDebarkerPlan/debarker_anim.py</c>, which names itself the source of this table, and the movement spec
/// <c>AutoDebarkerPlan/07-visual-sound/01-anim-spec.md</c>. <see cref="DebarkerPartGroup.Static"/> is the frame baked
/// into the chunk mesh. <see cref="DebarkerPartGroup.Workpiece"/> is the log: it stays out of that mesh and out of the
/// movers; the master builds it painted by the loaded log's species, one CPU mesh per stem (the core, each strip;
/// <see cref="LogGroupFor"/>, T07.04), and <see cref="DebarkerRenderer"/> draws it. The <c>frame</c> and <c>spindle</c>
/// rules together cover what the <c>selectiveElements</c> masks of <c>frame.json</c> keep.
///
/// <para>A name no rule matches falls silently into the frame, so a part renamed in Blender freezes in place (memory
/// vs-iw-custom-model-locations); <c>debarker_anim.py</c> has no catch-all and catches the rename.</para>
///
/// <para>Order is a contract: the renderer indexes the matrices from <see cref="BlockEntityDebarker.GetPartMatrices"/>
/// by a rule's position in <see cref="Rules"/>, and the frame must stay last (<see cref="FrameIndex"/>).
/// <c>debarker_anim.py</c> and the spec's table list <c>frame</c> first, so T07.03 builds its driver table in this
/// array's order, not theirs.</para>
/// </summary>
internal static class DebarkerParts
{
    // The export is flat. The underscore separates the stem from the Blender suffix,
    // so "knife" cannot take the disc's "knife-nut", nor "pedal" the frame's "pedal-hinge".
    internal static readonly DebarkerPartRule[] Rules =
    {
        new("spindle", DebarkerPartGroup.Spindle, "shaft", "coupling"),
        new("disc", DebarkerPartGroup.Disc, "disc", "hub", "knife-nut"),
        new("knives", DebarkerPartGroup.Knives, "knife"),
        new("gearMain", DebarkerPartGroup.Drive, "gear-main"),
        new("gearIdler", DebarkerPartGroup.Drive, "gear-idler"),
        new("idlerPin", DebarkerPartGroup.Drive, "idlerpin"),
        new("pinion", DebarkerPartGroup.Drive, "pinion", "countershaft", "crown"),
        new("vshaft", DebarkerPartGroup.Drive, "lantern", "vshaft", "worm"),
        new("feed3", DebarkerPartGroup.Feed, "feed3", "wormwheel"),
        new("feed2", DebarkerPartGroup.Feed, "feed2"),
        new("feed1", DebarkerPartGroup.Feed, "feed1"),
        new("liftFrame", DebarkerPartGroup.Feed, "liftframe"),
        new("liftShaft", DebarkerPartGroup.Feed, "liftshaft"),
        new("cam", DebarkerPartGroup.Feed, "cam", "camshaft", "lever"),
        new("rod", DebarkerPartGroup.Feed, "rod"),
        new("pedal", DebarkerPartGroup.Feed, "pedal"),
        new("log", DebarkerPartGroup.Workpiece, "log-core", "log-bark1", "log-bark2", "log-bark3",
            "log-bark4", "log-bark5", "log-bark6", "log-bark7", "log-bark8"),
        new("frame", DebarkerPartGroup.Static)
    };

    internal static int FrameIndex => Rules.Length - 1;

    /// <summary>The log's rule. Its stems list the core first and then the bark strips by number, which
    /// <see cref="LogGroupFor"/> relies on.</summary>
    internal static readonly int LogIndex = Array.FindIndex(Rules, r => r.Group == DebarkerPartGroup.Workpiece);

    /// <summary>The number of bark strips around the log's core, 8. The log shows <c>LogStripCount − floor(progress ×
    /// LogStripCount)</c> of them: with the divisor equal to the strip count the last strip goes out only with the log
    /// itself (spec T07.01).</summary>
    internal static int LogStripCount => Rules[LogIndex].Stems.Length - 1;

    internal static int IndexFor(string? name) => Match(name, out int rule, out _) ? rule : FrameIndex;

    /// <summary>The log's mesh group of an element (T07.04): 0 for the core, k for bark strip k (1 to
    /// <see cref="LogStripCount"/>), −1 for an element of any other rule. The group is the position of the matching
    /// stem in the log rule.</summary>
    internal static int LogGroupFor(string? name) => Match(name, out int rule, out int stem) && rule == LogIndex ? stem : -1;

    // The rule an element belongs to and the position of the stem that claimed it. False for a name no rule claims,
    // which the frame takes.
    static bool Match(string? name, out int rule, out int stem)
    {
        const string prefix = "debarker-";
        if (name != null)
        {
            int suffix = name.IndexOf('_');
            if (suffix > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                var span = name.AsSpan(prefix.Length, suffix - prefix.Length);
                for (rule = 0; rule < FrameIndex; rule++)
                {
                    string[] stems = Rules[rule].Stems;
                    for (stem = 0; stem < stems.Length; stem++)
                        if (span.SequenceEqual(stems[stem])) return true;
                }
            }
        }
        rule = FrameIndex;
        stem = -1;
        return false;
    }
}

using System.Diagnostics;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ImmersiveWoodworking;

/// <summary>
/// Diagnostic tracing for the birch-tar rig (BarkPlan/E04), added 2026-07-28 to chase the playtest report
/// "assembled rigs collapse into a flat plane with the ? texture, and colours scramble".
///
/// Gated behind the IW_DEBUG compile symbol exactly like <see cref="BlockChoppingBlock"/>'s Dbg/DbgLog: a plain
/// `dotnet build` (and the shipped Release) strips every call AND its arguments at compile time, so there is zero
/// runtime cost and nothing can leak into a release. Turn it on with `dotnet build -p:IwDebug=true`, which also
/// redirects the output to bin\$(Configuration)-iwdebug\Mods\mod so a logging build can never be shipped by mistake.
///
/// Everything is tagged <c>[IW-TAR]</c> with the side, so one grep over client-main.log + server-main.log gives the
/// whole story: what the server stored on the rig stack, what the client read back off it, which textures resolved,
/// and how many vertices the resulting mesh actually has (a "flat plane" is ~4).
/// </summary>
static class TarDbg
{
    public const string Tag = "[IW-TAR]";

    // NOTE (learned the hard way, first trace run 2026-07-29): ILogger.Notification(string) is NOT a plain write —
    // the single-argument overload still routes through Log(type, message, emptyArgs), i.e. string.Format
    // (ILogger.cs:239). Any '{' in the message is therefore parsed as a format placeholder, and our attribute dump
    // ("attrs{...}") made every interesting line die with a FormatException — the log kept only the lines that
    // happened to contain no braces. So ALWAYS pass the text as an ARGUMENT of a "{0}" format, never as the format
    // itself; then braces in the payload are data, not syntax.
    [Conditional("IW_DEBUG")]
    public static void Log(ICoreAPI? api, string msg)
    {
#if IW_DEBUG
        if (api == null) return;
        api.Logger.Notification("{0}", $"{Tag} {api.Side} {msg}");
#endif
    }

    [Conditional("IW_DEBUG")]
    public static void Log(IWorldAccessor? world, string msg)
    {
#if IW_DEBUG
        if (world == null) return;
        world.Logger.Notification("{0}", $"{Tag} {world.Side} {msg}");
#endif
    }

    /// <summary>Full identity of a stack: class, code, size and EVERY attribute key/value. This is the probe that
    /// answers "did the rig's colour attributes survive the trip to the client / the pickup and re-place".</summary>
    public static string Describe(ItemStack? stack)
    {
#if IW_DEBUG
        if (stack == null) return "<null stack>";
        var sb = new StringBuilder();
        sb.Append(stack.Class).Append(':');
        // Collectible is null on an UNRESOLVED stack — exactly the state that renders as the vanilla "?" quad, so
        // report it explicitly rather than throwing.
        sb.Append(stack.Collectible?.Code?.ToString() ?? "<UNRESOLVED collectible, Id=" + stack.Id + ">");
        sb.Append(" x").Append(stack.StackSize);
        sb.Append(" attrs{");
        bool first = true;
        foreach (var kv in stack.Attributes)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(kv.Key).Append('=');
            // Nested itemstack attributes (the rig's stored receiver) print as their code rather than a type name.
            if (kv.Value is ItemstackAttribute isa) sb.Append("stack(").Append(isa.value?.Collectible?.Code?.ToString() ?? "<unresolved>").Append(')');
            else sb.Append(kv.Value?.ToString() ?? "null");
        }
        sb.Append('}');
        return sb.ToString();
#else
        return "";
#endif
    }
}

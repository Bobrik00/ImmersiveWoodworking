using Vintagestory.API.Common;

namespace ImmersiveWoodworking;

/// <summary>
/// Allocation-free reads of one dash-separated part of an asset code (AuditFixPlan T05.03 / F-L10-01).
///
/// <para>Why this exists: <see cref="RegistryObject.FirstCodePart(int)"/> — the idiomatic way to ask "is this a
/// log?" — runs <c>Code.Path.Split('-')</c> on every call, so ONE question about <c>log-placed-oak-ud</c> builds a
/// <c>string[]</c> plus four substrings. That is free at load time and it is not free on the three paths that ask it
/// tens of times a second per machine: the sawmill's server heartbeat (<c>BlockEntitySawmill.CanSaw</c>, every
/// 25 ms), the client frame (the same predicate twice per frame, plus the chopper's <c>CanChop</c> behind its
/// effects) and the manual hold step (<c>BlockEntitySawhorse.CanSawHere</c>, re-checked per tick of the hold). The
/// sawmill's start gate alone allocated 27 objects per call, nearly all of them code parts nobody kept.</para>
///
/// <para>These helpers answer the SAME questions by scanning the path in place: <see cref="Is"/>,
/// <see cref="IsAny"/> and <see cref="EndsWith"/> allocate nothing at all, and <see cref="At"/> allocates only the
/// one substring its caller actually keeps. Semantics are 1:1 with <c>FirstCodePart(n)</c>, including its two edge
/// cases — a code with no dash IS its own part 0, and an index past the last part yields "no value" (null / no
/// match) rather than throwing. A null code, or a null <c>Code</c> on the object, is simply "no match", so the
/// callers keep the null-tolerance they had.</para>
///
/// <para>Deliberately NOT applied mod-wide. Every load-time and once-per-gesture caller keeps the engine idiom,
/// which reads better and costs nothing there; this is for the hot paths only.</para>
/// </summary>
internal static class CodeParts
{
    /// <summary>Whether code part <paramref name="index"/> of <paramref name="code"/> equals
    /// <paramref name="value"/>, compared ordinally. The alloc-free form of
    /// <c>obj.FirstCodePart(index) == value</c>.</summary>
    internal static bool Is(AssetLocation? code, int index, string value)
    {
        string? path = code?.Path;
        if (path == null || !TryPart(path, index, out int start, out int len)) return false;
        return len == value.Length && string.CompareOrdinal(path, start, value, 0, len) == 0;
    }

    /// <summary>Whether code part <paramref name="index"/> equals ANY of <paramref name="values"/> — the alloc-free
    /// form of <c>Array.IndexOf(values, obj.FirstCodePart(index)) >= 0</c>, for the accept lists that name several
    /// first parts at once (<c>BlockEntityChoppingBlock.LogCodeParts</c>).</summary>
    internal static bool IsAny(AssetLocation? code, int index, string[] values)
    {
        string? path = code?.Path;
        if (path == null || !TryPart(path, index, out int start, out int len)) return false;

        foreach (string value in values)
        {
            if (len == value.Length && string.CompareOrdinal(path, start, value, 0, len) == 0) return true;
        }
        return false;
    }

    /// <summary>Whether code part <paramref name="index"/> ENDS with <paramref name="suffix"/>, compared ordinally —
    /// the alloc-free form of <c>obj.FirstCodePart(index).EndsWith(suffix, StringComparison.Ordinal)</c>. Carries the
    /// "recognised by shape, never by a species list" rule that matches both <c>firewood</c> and a wood mod's own
    /// <c>{wood}firewood</c> (source-conventions.md 2).</summary>
    internal static bool EndsWith(AssetLocation? code, int index, string suffix)
    {
        string? path = code?.Path;
        if (path == null || !TryPart(path, index, out int start, out int len)) return false;
        return len >= suffix.Length
               && string.CompareOrdinal(path, start + len - suffix.Length, suffix, 0, suffix.Length) == 0;
    }

    /// <summary>Whether the LAST code part equals <paramref name="value"/>, compared ordinally — the alloc-free form
    /// of <c>obj.LastCodePart() == value</c>. The last part is where the mod's own gates read a state or an
    /// orientation (a fired pot, a driftwood size, a log's ud/we/ns).</summary>
    internal static bool IsLast(AssetLocation? code, string value)
    {
        string? path = code?.Path;
        if (path == null) return false;

        int start = path.LastIndexOf('-') + 1; // no dash → -1 + 1 = 0, i.e. the whole path, exactly as LastCodePart()
        int len = path.Length - start;
        return len == value.Length && string.CompareOrdinal(path, start, value, 0, len) == 0;
    }

    /// <summary>The LAST code part as a string — the answer of <c>obj.LastCodePart()</c> for one substring instead of
    /// a whole split array. Null only when the code itself is null.</summary>
    internal static string? Last(AssetLocation? code)
    {
        string? path = code?.Path;
        return path?.Substring(path.LastIndexOf('-') + 1);
    }

    /// <summary>Code part <paramref name="index"/> as a string, or null when the code has fewer parts than that —
    /// the exact answer of <c>obj.FirstCodePart(index)</c>, at the cost of the ONE substring the caller keeps
    /// instead of a whole split array. Use only where the part is genuinely needed as a value (a wood species, a
    /// tool family); to merely compare it, use <see cref="Is"/>.</summary>
    internal static string? At(AssetLocation? code, int index)
    {
        string? path = code?.Path;
        if (path == null || !TryPart(path, index, out int start, out int len)) return null;
        return path.Substring(start, len);
    }

    /// <summary>Locate code part <paramref name="index"/> inside <paramref name="path"/>: walk <paramref name="index"/>
    /// dashes from the left, then measure to the next dash or to the end. False when the path runs out of parts first,
    /// which is where <c>FirstCodePart</c> returns null. Index 0 on a dash-less path spans the whole path, again as
    /// the engine does.</summary>
    static bool TryPart(string path, int index, out int start, out int len)
    {
        start = 0;
        len = 0;

        for (int i = 0; i < index; i++)
        {
            int dash = path.IndexOf('-', start);
            if (dash < 0) return false;
            start = dash + 1;
        }

        int end = path.IndexOf('-', start);
        len = (end < 0 ? path.Length : end) - start;
        return true;
    }
}

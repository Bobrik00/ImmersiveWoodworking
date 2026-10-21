using Vintagestory.API.Config;

namespace ImmersiveWoodworking;

/// <summary>
/// The mod's one way of saying "&lt;Species&gt; &lt;thing&gt;" — "Oak Half-log", "Birch Chopping Block", "Maple
/// Half-log slab". ONE template per language and per thing, ZERO per-species strings, so a wood mod nobody has
/// written yet is named correctly with no content from us. Vanilla does the opposite for the same shape of block
/// (<c>block-plankslab-oak-*</c>, <c>block-plankslab-birch-*</c>: 14 hand-written lines for its 12 species plus
/// <c>aged</c>/<c>veryaged</c>), which is affordable for a closed set and is not for a set any mod can extend.
///
/// <para>Extracted in T03.04 (HalfLogBlockPlan). Before it, <see cref="ItemHalfLog"/> and
/// <see cref="BlockChoppingBlock"/> each held a verbatim copy of the resolver AND of <c>CapitalizeFirst</c>, and the
/// half-log slab would have made a third — which the T03.03 review named and deferred to exactly here. The lookup
/// chain is what those two ran before, with ONE deliberate difference: an EMPTY <c>material-*</c> entry is now
/// treated as absent instead of producing a name with a leading space. Inert on everything measurable (zero empty
/// <c>material-*</c> values across 201 lang files in vanilla plus every mod on disk) and it makes this agree with the
/// self-check that judges it, which tests <c>IsNullOrEmpty</c>. The capitalisation those copies also carried survives
/// on ONE branch only — see <see cref="Species"/> (AuditFixPlan T09.05).
///
/// <para>What one template per thing does NOT mean: it does not force the species to sit in front like an
/// ungrammatical adjective. Only the SUBSTITUTION is shared; the word order is authored per language, and the mod's
/// own files already differ — en "{0} Half-log", ru "Половинка бревна ({0})", de "Halber Stamm ({0})", zh-cn
/// "{0}半截原木", pt-br "Meia-tora de {0}". So the real cost is one line per language per thing, and what it buys is
/// that no wood mod ever needs a patch from us to be named.
/// </summary>
static class WoodName
{
    /// <summary>
    /// Fill a "{0} something" lang template with a species word. <paramref name="templateKey"/> is domain-qualified
    /// (<c>immersivewoodworking:item-halflog-name</c>); <paramref name="wood"/> is the BARE species code and
    /// <paramref name="woodDomain"/> the domain that ships it — the same pair a half-log stack carries in its
    /// attributes and a slab variant reconstructs from its code.
    /// </summary>
    public static string InTemplate(string templateKey, string wood, string woodDomain)
        => Lang.Get(templateKey, Species(wood, woodDomain));

    /// <summary>
    /// The species word for a name: whatever <see cref="Translated"/> resolved, passed on in the case the LANG FILE
    /// wrote it, with a leading capital added on ONE branch — the raw species code standing in for a name nobody
    /// translated. Never blank, so a wood that ships no material name at all is still named.
    ///
    /// <para>Case is a property of the language, and it already lives in the language's own data (AuditFixPlan
    /// T09.05, F-L3-14). The word lands MID-STRING in three of the five languages the mod ships (ru "Половинка
    /// бревна ({0})", de "Halber Stamm ({0})", pt-br "Meia-tora de {0}") and leads the string in the other two, and
    /// every file already spells its own convention: vanilla writes en "Oak", de "Eiche", pt-br "Carvalho" with a
    /// capital and ru "дуб" without. Forcing one here turned every Russian name into "Половинка бревна (Дуб)", which
    /// the game itself never writes — <c>BehaviorDoor.cs:489</c> pastes <c>Lang.Get("material-" + wood)</c> into
    /// <c>doorname-with-material</c> ("{0} ({1})" in ru, the same shape as ours) with no case conversion at all, and
    /// so do the trapdoor, figurehead, lantern and antler-mount names.
    ///
    /// <para>The fallback keeps its capital because there is no translator behind it: a species with no
    /// <c>material-*</c> entry in any domain is named by its own lowercase code, and it is the one branch no lang
    /// file — ours or a wood mod's — can reach in and fix. What a foreign mod writes in ITS <c>material-*</c> is its
    /// call and stays untouched, exactly as vanilla leaves it (wildcrafttree capitalises all 44 of its Russian
    /// species; its doors read the same way).
    ///
    /// <para>Deliberately not public: a caller that wants the word alone almost always wants a full name, and the one
    /// place that genuinely needs an independent lookup — the IW_DEBUG name check in <see cref="BlockHalfLogSlab"/> —
    /// must not build its ORACLE out of this, because an oracle made of the code under test proves nothing.
    /// </summary>
    static string Species(string wood, string woodDomain)
        => Translated(wood, woodDomain) ?? CapitalizeFirst(wood);

    /// <summary>The species word as the game writes it, falling back to the RAW species code — the same chain
    /// <see cref="Species"/> runs, minus the capital that only the untranslated fallback gets. The handbook's "which
    /// woods drop this bark" list (AuditFixPlan T09.01) is the caller: it sets the words inside a sentence-like list,
    /// where a bare code reads better lowercase than a name at the head of a label would.</summary>
    internal static string SpeciesWord(string wood, string woodDomain)
        => Translated(wood, woodDomain) ?? wood;

    /// <summary>
    /// The <c>material-{wood}</c> entry, or null when no domain ships one. Looked up BARE first — so vanilla and the
    /// wood mods that register their material-* under <c>game:</c> (wildcrafttree does, all 44 of them) both hit —
    /// then in the species' own domain, for a mod that keeps them to itself, and finally in OURS.
    ///
    /// <para>The third step exists because vanilla's own coverage has holes (AuditFixPlan T04.05, F-L7-12): it ships
    /// <c>material-aged</c> and <c>material-veryaged</c> but no <c>material-rotten</c> / <c>material-veryrotten</c>,
    /// even though both are real log woods with fully translated BLOCK names — so a rotten half-log was called by its
    /// raw English code in all five languages. It is LAST on purpose: a name that vanilla or the wood's own mod ships
    /// always wins, so filling a gap here can never override somebody else's translation, and the two words we do
    /// ship stop being read the moment the game adds its own.
    /// </summary>
    static string? Translated(string wood, string woodDomain)
        => NullIfEmpty(Lang.GetIfExists("material-" + wood))
        ?? NullIfEmpty(Lang.GetIfExists(woodDomain + ":material-" + wood))
        ?? NullIfEmpty(Lang.GetIfExists("immersivewoodworking:material-" + wood));

    /// <summary>A lang entry that exists but is blank is no name at all; treat it as a miss so the chain moves on to
    /// the next lookup instead of composing a name that opens with a stray space.</summary>
    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    static string CapitalizeFirst(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
}

namespace ImmersiveWoodworking;

/// <summary>
/// The four resolved chop-drop amounts the SHARED chop resolver needs (firewood per log + its pile count, sticks per
/// firewood + its pile count), passed IN so <see cref="BlockEntityChoppingBlock.GetChopResult"/> /
/// <see cref="BlockEntityChoppingBlock.MakeSticks"/> read no drop config field directly (ImprovementsPlan E04/T04.05).
///
/// Threading the resolved NUMBERS — rather than a "which station am I" flag or a second copy of the resolver — is how
/// the manual chopping block and the automated chopper can yield DIFFERENT amounts while sharing ONE resolver: the hand
/// block builds this from its manual fields (<see cref="Manual"/>), the machine from its -1-inherit overrides
/// (<see cref="Chopper"/>). The resolver stays a pure function of its input, so the two tiers can never drift in code,
/// and at the shipped defaults (every override -1 → inherit) both structs are identical and the machine drops exactly
/// what the hand block does.
///
/// This carries DROP amounts only. Manual-station axe WEAR (<see cref="BlockEntityChoppingBlock.SplitWearCost"/> /
/// <see cref="BlockEntityChoppingBlock.ChopWearCost"/>) is deliberately NOT routed through here — it stays on the
/// manual <see cref="ImmersiveWoodworkingConfig.FirewoodPerLog"/>, because wear is a hand-station concept (the machine
/// has no player axe) and must never follow a chopper override.
/// </summary>
internal readonly struct DropAmounts
{
    public readonly int FirewoodPerLog;
    public readonly int FirewoodDropCount;
    public readonly int SticksPerFirewood;
    public readonly int SticksDropCount;

    public DropAmounts(int firewoodPerLog, int firewoodDropCount, int sticksPerFirewood, int sticksDropCount)
    {
        FirewoodPerLog = firewoodPerLog;
        FirewoodDropCount = firewoodDropCount;
        SticksPerFirewood = sticksPerFirewood;
        SticksDropCount = sticksDropCount;
    }

    /// <summary>The HAND chopping block's amounts — the manual config fields verbatim. This is the tier reference the
    /// automated overrides inherit from at their -1 default, so <see cref="Chopper"/> equals this out of the box.</summary>
    public static DropAmounts Manual(ImmersiveWoodworkingConfig c)
        => new DropAmounts(c.FirewoodPerLog, c.FirewoodDropCount, c.SticksPerFirewood, c.SticksDropCount);

    /// <summary>The automated CHOPPER's amounts — each override resolved through its <c>*Effective</c> accessor, so a
    /// -1 field inherits the matching manual field and a 0+ field wins. At the shipped defaults this is byte-identical
    /// to <see cref="Manual"/>.</summary>
    public static DropAmounts Chopper(ImmersiveWoodworkingConfig c)
        => new DropAmounts(c.ChopperFirewoodPerLogEffective, c.ChopperFirewoodDropCountEffective,
                           c.ChopperSticksPerFirewoodEffective, c.ChopperSticksDropCountEffective);
}

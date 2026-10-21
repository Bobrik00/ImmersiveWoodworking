using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// Turning a PLACED half-log slab with the vanilla wrench (HalfLogBlockPlan E05), plus the world-edit / schematic
/// transforms that answer the same question for a whole selection. Both change nothing but the orientation parts of
/// <c>halflogslab-{wood}-{skin}-{rot}-{run}-{face}</c>; the species AND the bark state are preserved structurally,
/// because every code here is built by <see cref="BlockHalfLogSlab.CodeWithState"/> out of this very block's own
/// <see cref="WoodCode"/> and <see cref="SkinCode"/>. That is why the skin axis (AuditFixPlan T04.01) cost this file
/// no gesture and no branch: a wrench turns a stripped slab into another stripped slab without being told to.
///
/// <para><b>Why the vanilla JSON <c>WrenchOrientable</c> behaviour is NOT attached, contrary to T05.01's step 1.</b>
/// Its <c>baseCode</c> is not a pattern of any kind and is never matched against anything: it is an opaque string KEY
/// into a process-wide <c>Dictionary&lt;string, SortedSet&lt;AssetLocation&gt;&gt;</c> that every block carrying the
/// behaviour writes its own code into on load (vssurvivalmod BehaviorWrenchOrientable.cs:31, 115-121). The literal
/// <c>*</c> in vanilla's <c>"quartzslab-*-{cover}"</c> is a mnemonic character, not a wildcard. Three consequences,
/// each on its own fatal here:
/// <list type="number">
/// <item>The cycle order is the ORDINAL SORT of the full codes, so the best possible key (<c>halflogslab-{wood}</c>,
/// one group per species) yields <c>down-ns-bark, down-ns-split, down-we-bark, down-we-split, east-ns-bark, …</c> —
/// the block jumps from the bottom half of the cell to the east half on the fifth click, and closing the loop takes
/// 48 (24 before the skin axis doubled the family). T05.02 asks for exactly the opposite ("соседние шаги визуально отличались предсказуемо, а не прыгали").</item>
/// <item>The direction argument is DISCARDED on that path: <c>ItemWrench</c> walks the set forward with
/// <c>MoveNext()</c> and wraps with <c>.First()</c>, never reading <c>dir</c> (ItemWrench.cs:214-238). So there is no
/// reverse, in a 48-long cycle.</item>
/// <item>There is no modifier. One key, one order, one gesture.</item>
/// </list>
/// The one thing that mechanism does give for free is that a <c>skipVariants</c> hole is unreachable by construction
/// (a variant that does not exist never registers) — and that guarantee is reproduced here by DERIVING legality from
/// <c>run.Axis != rot.Axis</c> rather than by probing, which is what the asset's six skip patterns (twelve removed
/// variants, since each pattern spans both <c>face</c> values) say in the first place; see
/// <see cref="HalfLogPlacement.RunFor"/>.
///
/// <para><b>Why implementing the interface reaches the wrench at all.</b> <c>ItemWrench.rotate</c> asks
/// <c>block.GetInterface&lt;IWrenchOrientable&gt;()</c> FIRST and only falls through to the behaviour when that is
/// null (ItemWrench.cs:206-215), and <c>Block.GetInterface</c> tests <c>this is T</c> before behaviours, block entity
/// and block-entity behaviours (vsapi Block.cs:2706-2723). A Block that implements the interface therefore wins
/// outright. <c>IWrenchOrientable</c> is declared in VintagestoryAPI.dll under namespace
/// <c>Vintagestory.GameContent</c> (vsapi Common/Collectible/Block/IWrenchOrientable.cs); no new project reference.
///
/// <para><b>The gestures.</b> Each one changes exactly ONE conceptual thing, always — which is the strongest reading
/// of T05.02's "предсказуемо, а не прыгали", and the reason a single flat cycle over the whole family was
/// rejected even with our own code driving it.
/// <list type="bullet">
/// <item><b>Right-click</b> — flip <c>face</c>: bark side out or split side out. The one axis a placement cannot
/// reach at all (<see cref="HalfLogPlacement.DefaultFace"/> is always <c>split</c>), so it gets the primary gesture.
/// </item>
/// <item><b>Left-click</b> — flip <c>run</c> to the other axis that is legal for this <c>rot</c>. Exactly two are,
/// always, so this is an involution too.</item>
/// <item><b>Shift + right / left click</b> — step <c>rot</c> forward / backward through <c>BlockFacing.ALLFACES</c>,
/// carrying <c>run</c> where it stays legal. This is the only tier that MOVES the filled half of the cell, which is
/// why it is behind a modifier.</item>
/// </list>
/// Both flips are their own inverse, so nothing is lost by not spending left-click on "undo the face flip": undoing
/// it IS clicking again. The Shift tier does need a reverse, and gets it.
///
/// <para><b>Why <c>dir</c> is read as a BUTTON on the plain tier and as a DIRECTION under Shift.</b> The interface is
/// handed <c>+1</c> from <c>OnHeldAttackStart</c> (left mouse, ItemWrench.cs:111) and <c>-1</c> from
/// <c>OnHeldInteractStart</c> (right mouse, :190); it is the only signal that tells the two buttons apart, and vanilla
/// implementers use it as a direction because their gestures happen to be single-axis
/// (<c>BEConditional.cs:107 facing = dir &gt; 0 ? facing.GetCCW() : facing.GetCW();</c>). Our Shift tier follows that
/// convention verbatim — stepping <c>ALLFACES</c> by <c>-dir</c> makes right-click go north → east → south → west,
/// i.e. clockwise seen from above, which is precisely what <c>GetCW</c> gives vanilla's right-click.
///
/// <para><b>Why the world is changed on the SERVER only</b>, unlike vanilla, which mutates on whichever side runs
/// (<c>ItemWrench.rotate</c> calls <c>ExchangeBlock</c> unconditionally at :231).
///
/// <para>NOT because the two sides could read <c>ShiftKey</c> differently. They cannot, and the first version of this
/// comment said they could: control flags do not ride the UDP position packet at all. The client's own
/// <c>Controls.ShiftKey</c> is written once per 20 ms tick by <c>SystemPlayerControl.OnGameTick</c>
/// (<c>entityControls.ShiftKey = game.KeyboardState[shiftKey]</c>), and the very next statement in that same method
/// sends the changed flags as an ordered, reliable <c>Packet_MoveKeyChange</c> on the same TCP stream the click
/// packet uses. So for any given click the server holds exactly the flag value the client dispatched on. Vanilla
/// relies on this too — <c>BlockChisel.Rotate</c> reads <c>byEntity.Controls.CtrlKey</c> inside its own
/// <c>IWrenchOrientable</c> implementation.
///
/// <para>The real reason is that a refusal is one-sided and unrecoverable. Everything this method decides is
/// deterministic and shared, but everything that can VETO the click is server-only: the land-claim test (claim data
/// on a client can lag) and the server's own <c>TriggerHandInteraction</c> event, which any third-party mod can
/// answer with <c>PreventDefault</c> and which has no client counterpart at all. A client that predicted a turn the
/// server then refused would be left rendering a block the server does not have — and this block has no block entity,
/// so nothing ever re-syncs it; it stays wrong until that chunk is reloaded. Vanilla accepts that risk on every
/// wrenchable slab. We do not have to, and the mod already spends the same round trip on the far more frequent
/// gesture, its own placement (<c>ItemHalfLog.OnHeldInteractStart</c>), so the feel stays consistent.
///
/// <para>Two costs, both real. One round trip before the block visibly turns. And <c>ItemWrench</c>'s own
/// <c>RedrawNeighbouringChunk</c> (:245) now fires against a block that has not changed yet, so the edge redraw it
/// exists for is lost — which is why <see cref="RegisterEdgeRedraw"/> below re-does it when the change actually
/// lands, for every client rather than only the acting one.
///
/// <para><b>Land claims are NOT re-tested here</b>, deliberately, and this is the one place in this mod where a
/// redundant claim check was considered and rejected. <c>ItemWrench</c> tests <c>Claims.TryAccess(…, BuildOrBreak)</c>
/// on BOTH of its entry points before it can reach us (ItemWrench.cs:97 left, :151 right), and it is the only caller
/// of this interface anywhere in the game, in vssurvivalmod or in any mod unpacked in <c>reference-mods/</c>. So the
/// server-side gate a second call would provide is already fired, including its <c>CanPlaceOrBreakBlock</c> event —
/// and <c>TryAccess</c> MESSAGES the player itself, so a second call on the only path that could ever reach it would
/// say the same refusal twice. T04.02 kept its redundant check for the opposite reason: there the engine's own gate
/// (<c>Block.CanPlaceBlock</c>) collapsed every refusal into one vague code, and ours was the specific one. Here the
/// engine's gate IS the specific one.
/// </summary>
public partial class BlockHalfLogSlab : IWrenchOrientable
{
    // The two `face` states, tied to the placement default so the pair cannot drift apart in one file only.
    const string SplitFace = HalfLogPlacement.DefaultFace;
    const string BarkFace = "bark";

    // The three cartesian axes, for "the axis that is neither of these two". Written as a list rather than as
    // arithmetic on the enum's numeric values (`3 - rotAxis - runAxis` is correct today) because that arithmetic is
    // silently wrong the day EnumAxis is renumbered, and this is not a hot path.
    static readonly EnumAxis[] AllAxes = { EnumAxis.X, EnumAxis.Y, EnumAxis.Z };

    /// <summary>
    /// One wrench click on a placed slab. Called on BOTH sides by <c>ItemWrench</c> (see the class doc for why we
    /// answer on the server only), always after its own land-claim test, and always after it has already played the
    /// place sound (<c>ItemWrench.cs:242</c>, before the call at :244).
    ///
    /// <para><b>A refusal here is not free, but not for the obvious reason.</b> The durability charge happens AFTER
    /// us, on the way out (<c>DamageItem</c> at :115 for left mouse and :194 for right, and skipped entirely in
    /// creative) — so at the moment this method runs, nothing has been spent yet. It is spent anyway, because
    /// <c>ItemWrench.rotate</c> reports success unconditionally on the interface branch (:210-211) and the interface
    /// returns <c>void</c>, giving us no way to signal a refusal. That is why every branch below is TOTAL over the 24
    /// registered states rather than validating and declining: the only way to reach an early return is a registry
    /// that no longer holds this species' variants, which <see cref="Get"/> already reports.
    /// </summary>
    public void Rotate(EntityAgent byEntity, BlockSelection blockSel, int dir)
    {
        if (blockSel == null) return;

        IWorldAccessor world = byEntity.World;
        if (world.Side != EnumAppSide.Server) return;

        string? rot = Variant["rot"], run = Variant["run"], face = Variant["face"];
        if (rot == null || run == null || face == null) return;

        BlockFacing? rotFacing = BlockFacing.FromCode(rot);
        if (rotFacing == null) return;

        // The SEAT's controls when mounted, the entity's own otherwise. Not defensive padding: mounting clears the
        // rider's own flags (`doMount` -> `controls.StopAllMovement()`, vsapi EntityAgent.cs:294, and that loop clears
        // every flag including ShiftKey at index 14), and from then on `SystemPlayerControl.OnGameTick` writes the
        // keyboard into `MountedOn.Controls` instead — so a rider's own `Controls.ShiftKey` is frozen false. Reading
        // it directly would make both Shift gestures silently perform the two plain ones for anyone in a boat, on a
        // saddled animal or on an elevator platform. Any future Controls read in this mod has the same trap.
        EntityControls controls = byEntity.MountedOn?.Controls ?? byEntity.Controls;

        if (controls.ShiftKey)
        {
            (rot, run) = StepRot(rotFacing, run, dir);
        }
        else if (dir > 0)
        {
            // Left mouse: the other axis legal here. `?? run` cannot fire for a registered variant (the invariant
            // run.Axis != rot.Axis leaves exactly one third axis) and is here so a corrupted code degrades to a
            // no-op rather than to a null code.
            run = FlipRun(rotFacing, run) ?? run;
        }
        else
        {
            // Right mouse: bark out or split out.
            face = face == BarkFace ? SplitFace : BarkFace;
        }

        // WithState, not world.GetBlock(CodeWithState(...)): it is the one seam that logs a miss once per species
        // instead of once per click, which is exactly the promise its doc makes to callers (T03.03).
        Block? next = WithState(world, rot, run, face);
        if (next == null || next.Id == Id) return;

        // ExchangeBlock, not SetBlock: it swaps the block id in place without running the break path, so nothing
        // drops, the fluid layer and any reinforcement stay, and a decor on our one solid face survives. It notifies
        // nobody, though — hence the explicit neighbour update, the same line the placement path makes after
        // TryPlaceBlock (ItemHalfLog.cs). That call matters on the Shift tier: `rot` decides which single face is
        // solid (sideSolidOpaqueAoByType in the asset), so a torch or a sign hanging on the old face has to be told
        // its support moved.
        world.BlockAccessor.ExchangeBlock(next.Id, blockSel.Position);
        world.BlockAccessor.TriggerNeighbourBlockUpdate(blockSel.Position);
    }

    /// <summary>
    /// The next <c>(rot, run)</c> pair on the Shift tier: <c>rot</c> steps through <see cref="BlockFacing.ALLFACES"/>
    /// (north, east, south, west, up, down — vsapi Math/BlockFacing.cs:72, and <c>Index</c> IS the position in that
    /// array, :123), and <c>run</c> is carried unchanged whenever it stays legal at the new <c>rot</c>, else replaced
    /// by the axis the OLD <c>rot</c> used.
    ///
    /// <para><b>Why the replacement is always legal, and always reversible.</b> The branch fires only when
    /// <c>run.Axis == newRot.Axis</c>; the invariant on the block we started from says <c>run.Axis != oldRot.Axis</c>;
    /// therefore <c>oldRot.Axis != newRot.Axis</c> and the old rot's axis is a legal run at the new rot. Stepping back
    /// finds <c>run.Axis == oldRot.Axis</c>, fires the branch again and restores exactly the run we replaced — so the
    /// walk is a bijection, and Shift+left really is the undo of Shift+right.
    ///
    /// <para><b>The walk is one closed cycle of period 12 over all 12 legal pairs</b>, not two disjoint 6-cycles: the
    /// run carried around the six rots does not come home with them. From the creative state <c>down-we</c>, right
    /// mouse gives
    /// <c>down-we, north-we, east-ns, south-we, west-ns, up-ns, down-ns, north-ud, east-ud, south-ud, west-ud,
    /// up-we</c> and back. That is the whole reachable set, which is what makes E05's first acceptance criterion
    /// ("ключом перебираются все допустимые состояния") true: 12 pairs x 2 faces = the 24 registered states of one
    /// SKIN. The skin is not a wrench axis and never changes here, so the other 24 are reached by placing a stripped
    /// half-log rather than by turning a barked one inside out.
    /// <see cref="WrenchSelfCheck"/> proves the period and the coverage rather than trusting this paragraph.
    ///
    /// <para><paramref name="dir"/> is subtracted, not added, and that is vanilla's sign, not an inversion of it:
    /// right mouse arrives as <c>-1</c>, so <c>Index - dir</c> steps north → east → south → west, i.e. clockwise from
    /// above — the same thing <c>BEConditional.cs:107</c> gives right mouse with <c>GetCW()</c>.
    /// </summary>
    static (string Rot, string Run) StepRot(BlockFacing rot, string run, int dir)
    {
        BlockFacing next = BlockFacing.ALLFACES[GameMath.Mod(rot.Index - dir, BlockFacing.ALLFACES.Length)];

        string nextRun = HalfLogPlacement.RunAxis(run) != next.Axis
            ? run
            : HalfLogPlacement.RunCode(rot.Axis);

        return (next.Code, nextRun);
    }

    /// <summary>
    /// The other <c>run</c> legal for this <c>rot</c>: the axis that is neither the supporting face's nor the current
    /// run's. Exactly one exists for any registered variant, because the invariant <c>run.Axis != rot.Axis</c> leaves
    /// three axes minus two distinct ones. Null only for a code that violates that invariant, which is unreachable
    /// from the asset.
    /// </summary>
    static string? FlipRun(BlockFacing rot, string run)
    {
        EnumAxis runAxis = HalfLogPlacement.RunAxis(run);

        foreach (EnumAxis axis in AllAxes)
        {
            if (axis != rot.Axis && axis != runAxis) return HalfLogPlacement.RunCode(axis);
        }

        return null;
    }

    // ───────────────────────────── interaction help (T05.01) ─────────────────────────────
    //
    // `IWrenchOrientable` contributes NO interaction help — the "Rotate" line every other wrenchable block shows comes
    // from BlockBehaviorWrenchOrientable.GetPlacedBlockInteractionHelp (:81-101), which we are not attaching. Without
    // our own entries this block would advertise nothing at all, and `face` in particular is undiscoverable: it is
    // the one part no placement can produce.
    //
    // Attaching a bare `{ "name": "WrenchOrientable" }` with no properties WOULD have bought the vanilla line for free
    // (that is the clutter.json / figurehead.json idiom, and it is safe here because ItemWrench returns at :211 before
    // it can index the dictionary with a null BaseCode). Rejected on three counts: it adds a fourth, vaguer line
    // beside three specific ones; it would make `BlockBehaviors` non-empty for the first time on a class whose drop,
    // pick-block and placed-name loops all reproduce base's behaviour loop precisely BECAUSE the array is empty; and
    // that behaviour's OnUnloaded clears a process-wide static shared with every other mod (:124-128).

    /// <summary>The three wrench hints, resolved once client-side. Empty when no wrench item exists (a modpack could
    /// remove it), which is also what vanilla's help does in that case.</summary>
    WorldInteraction[] wrenchInteractions = Array.Empty<WorldInteraction>();

    /// <summary>
    /// Build the wrench hints. Client only — interaction help is a purely client-side overlay, and the process-wide
    /// cache means this costs one item search for the whole 1368-variant family rather than one per variant.
    /// </summary>
    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        if (api is not ICoreClientAPI) return;

        wrenchInteractions = ObjectCacheUtil.GetOrCreate(api, "iwHalfLogSlabWrenchInteractions", () =>
        {
            // `wrench-*` with no domain, i.e. game: only — copied verbatim from BehaviorWrenchOrientable.
            // loadWrenchItems (:50) so our icon strip is the same one every other wrenchable block shows. A wrench
            // shipped by a mod under its OWN domain is missed here exactly as it is missed by vanilla; it still
            // WORKS, because ItemWrench is the class, not the code, and only the hint's icons are affected.
            Item[] wrenches = api.World.SearchItems(new AssetLocation("wrench-*"));
            if (wrenches.Length == 0) return Array.Empty<WorldInteraction>();

            ItemStack[] stacks = new ItemStack[wrenches.Length];
            for (int i = 0; i < wrenches.Length; i++) stacks[i] = new ItemStack(wrenches[i]);

            return new WorldInteraction[]
            {
                new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-halflogslab-flipface",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = stacks
                },
                new WorldInteraction
                {
                    ActionLangCode = "immersivewoodworking:blockhelp-halflogslab-turnrun",
                    MouseButton = EnumMouseButton.Left,
                    Itemstacks = stacks
                },
                new WorldInteraction
                {
                    // Only the forward step is advertised. Shift+left is its exact inverse and would be a fourth line
                    // saying "the same thing, backwards"; the handbook (T06.02) is where that belongs.
                    ActionLangCode = "immersivewoodworking:blockhelp-halflogslab-nextrot",
                    // "shift", NOT "sneak". They are two separately rebindable hotkeys, and the gate above reads
                    // Controls.ShiftKey, which SystemPlayerControl feeds from `shift` (`sneak` feeds Controls.Sneak,
                    // a different flag). The HUD resolves this code to the player's CURRENT mapping, so naming the
                    // wrong one advertises the wrong key the moment a player separates the two in the controls
                    // settings. vsapi says which to use in as many words (EntityControls.cs:239-240: "Use
                    // Controls.ShiftKey instead for mouse interaction modifiers, as it is a separable control"), and
                    // vanilla agrees 66 to 1 across vssurvivalmod.
                    HotKeyCode = "shift",
                    MouseButton = EnumMouseButton.Right,
                    Itemstacks = stacks
                }
            };
        });
    }

    /// <summary>
    /// Show the wrench hints, unless the player could not build here anyway. The claim test mirrors
    /// <c>BehaviorWrenchOrientable.ShowWrenchRotateHelp</c> (:59-79) exactly, including its two narrowings:
    /// <c>TestAccess</c> rather than <c>TryAccess</c>, because <c>TryAccess</c> messages the player and re-syncs — a
    /// HUD hint must not do either — and survival only, because creative ignores claims.
    /// </summary>
    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        WorldInteraction[] fromBase = base.GetPlacedBlockInteractionHelp(world, selection, forPlayer);
        if (wrenchInteractions.Length == 0 || !CanWrenchHere(world, selection, forPlayer)) return fromBase;

        return wrenchInteractions.Append(fromBase);
    }

    /// <summary>
    /// Re-tesselate the NEIGHBOURING chunk when a half-log slab changes, client-side. Registered once from the mod
    /// system; the block cannot do it in <c>OnLoaded</c> because that runs per variant and would add 1368 handlers.
    ///
    /// <para><b>Why it is needed.</b> <c>ExchangeBlock</c> marks only the block's OWN chunk dirty (decompiled
    /// <c>BlockAccessorRelaxed.ExchangeBlock</c>: one <c>worldmap.MarkChunkDirty(pos.X / 32, pos.InternalY / 32,
    /// pos.Z / 32, priority: true)</c> and nothing else), unlike <c>SetBlock</c>, whose
    /// <c>SetSolidBlockInternal</c> calls <c>MarkAdjacentNeighboursDirty</c>. And <c>rot</c> is exactly what decides
    /// which single face of this block is solid and opaque (<c>sideSolidOpaqueAoByType</c> in the asset), i.e. what
    /// the neighbour culls its own face against. So a Shift turn on a chunk boundary leaves the block ACROSS that
    /// boundary with a face it culled against a solidity that is gone — a see-through hole that persists until
    /// something else dirties that chunk.
    ///
    /// <para><b>Why not just use <c>SetBlock</c> instead.</b> It would mark the neighbours, and it would also run
    /// <c>BreakAllDecorFast</c>, <c>OnBlockRemoved</c> / <c>OnBlockPlaced</c> and the liquid-displacement branch —
    /// i.e. it would destroy any decor on the slab and fire placement hooks for a block that was never placed.
    /// <c>ExchangeBlock</c> is the right primitive for an in-place swap; this is the piece it deliberately omits.
    ///
    /// <para><b>Why here and not in <c>Rotate</c>.</b> The turn happens on the server, where chunk meshes do not
    /// exist, and <c>ItemWrench</c>'s own compensating <c>RedrawNeighbouringChunk</c> (:245) fires on the acting
    /// client BEFORE the server's packet arrives, so it lands on an unchanged block. The client event fires from
    /// <c>HandleExchangeBlock</c> after the new id is in place, which is the only moment the redraw is worth
    /// anything — and it fires on EVERY client, so remote players are covered too. Vanilla covers neither: its
    /// wrenchable slabs leave the same stale edge on every non-acting client.
    ///
    /// <para>Cheap by construction: <c>RedrawNeighbouringChunk</c> only does work when the position is on a chunk
    /// edge, so an interior turn costs two type tests. Placing and breaking a slab go through <c>SetBlock</c> and are
    /// already covered; they simply pass through here harmlessly.
    /// </summary>
    internal static void RegisterEdgeRedraw(ICoreClientAPI capi)
    {
        capi.Event.BlockChanged += (pos, oldBlock) =>
        {
            // oldBlock may be null (the event's own doc says so), and the NEW block is what a turn produced, so both
            // ends are tested: a slab that turned, a slab that appeared, a slab that went away.
            if (oldBlock is BlockHalfLogSlab || capi.World.BlockAccessor.GetBlock(pos) is BlockHalfLogSlab)
            {
                capi.World.BlockAccessor.RedrawNeighbouringChunk(pos);
            }
        };
    }

    static bool CanWrenchHere(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        if (world.Claims == null) return true;
        if (world is not IClientWorldAccessor clientWorld) return true;
        if (clientWorld.Player?.WorldData.CurrentGameMode != EnumGameMode.Survival) return true;

        return world.Claims.TestAccess(clientWorld.Player, selection.Position, EnumBlockAccessFlags.BuildOrBreak)
               == EnumWorldAccessResponse.Granted;
    }

    // ───────────────────────────── world edit and schematics (T05.01, context paragraph) ─────────────────────────────
    //
    // A SEPARATE concern from the wrench, and worth saying so plainly: ItemWrench never calls any of the three methods
    // below. They are what `/we rotate`, `/we mirror` and every rotated schematic paste use, and the base
    // implementations return this block's own Code unchanged (vsapi Block.cs:2056-2078 and the two flips beside it),
    // so before this section a rotated half-log wall came out of a schematic still pointing the original way — and
    // silently, because BlockSchematic only warns about a code it cannot RESOLVE, never about one that did not move.
    //
    // NOTHING here can land on a skipVariants hole, and the proof is one line: all six transforms are signed
    // permutations of the coordinate axes, so they map distinct axes to distinct axes, and the only validity
    // condition on a variant is run.Axis != rot.Axis. WrenchSelfCheck re-derives that against the registry anyway,
    // because the three flip call sites dereference the resolved block with NO null check (BlockSchematic) and this
    // block is `canChisel: true`, so BEMicroBlock's material remap reaches them too.
    //
    // `face` is invariant under all six: it names which side points AWAY from the supporting face, and a rotation or
    // a mirror carries the support along with the block.

    /// <summary>
    /// This block turned <paramref name="angle"/> degrees about Y. Vertical rots are fixed points of a Y rotation;
    /// horizontal ones step through <see cref="BlockFacing.HORIZONTALS_ANGLEORDER"/>, and the log's axis swaps
    /// X &lt;-&gt; Z on odd quarter turns while <c>ud</c> stays put.
    ///
    /// <para>The horizontal step is vanilla's formula with its sign preserved: vanilla writes
    /// <c>HORIZONTALS_ANGLEORDER[((360 - angle) / 90 + oldFacing.HorizontalAngleIndex) % 4]</c>
    /// (BlockGroundAndSideAttachable.cs:169), and <c>(360 - angle) / 90 ≡ -quarters (mod 4)</c>, so
    /// <c>Mod(index - quarters, 4)</c> is the same map — written this way only so an angle outside 0..360, or one that
    /// is not a multiple of 90, cannot index out of range.
    /// </summary>
    public override AssetLocation GetRotatedBlockCode(int angle)
    {
        string? rot = Variant["rot"], run = Variant["run"], face = Variant["face"];
        if (rot == null || run == null || face == null) return Code;

        int quarters = GameMath.Mod(angle / 90, 4);
        if (quarters == 0) return Code;

        BlockFacing? rotFacing = BlockFacing.FromCode(rot);
        if (rotFacing == null) return Code;

        string newRot = rotFacing.IsVertical
            ? rot
            : BlockFacing.HORIZONTALS_ANGLEORDER[GameMath.Mod(rotFacing.HorizontalAngleIndex - quarters, 4)].Code;

        EnumAxis runAxis = HalfLogPlacement.RunAxis(run);
        string newRun = quarters % 2 == 0 || runAxis == EnumAxis.Y
            ? run
            : HalfLogPlacement.RunCode(runAxis == EnumAxis.X ? EnumAxis.Z : EnumAxis.X);

        return CodeWithState(newRot, newRun, face) ?? Code;
    }

    /// <summary>
    /// This block mirrored through a horizontal plane. Only the two vertical rots move (a floor slab becomes a ceiling
    /// slab); a wall slab is a fixed point, and no run axis changes, since a mirror maps each axis to itself.
    ///
    /// <para>Deliberately NOT vanilla's <c>BlockSlab.GetVerticallyFlippedBlockCode</c>, which reads
    /// <c>Variant["rot"] == "up" ? down : up</c> (vssurvivalmod Block/BlockSlab.cs:48-51): fed a wall rot the test is
    /// false, so a north-facing wall slab comes back as <c>rot = up</c> — a CEILING slab, since <c>up</c> is the top
    /// half of the cell. Vanilla gets away with it because its slabs with wall rots are rarely mirrored; ours would
    /// be silently wrong on every mirrored log wall.
    /// </summary>
    public override AssetLocation GetVerticallyFlippedBlockCode()
    {
        string? rot = Variant["rot"], run = Variant["run"], face = Variant["face"];
        if (rot == null || run == null || face == null) return Code;

        BlockFacing? rotFacing = BlockFacing.FromCode(rot);
        if (rotFacing == null || !rotFacing.IsVertical) return Code;

        return CodeWithState(rotFacing.Opposite.Code, run, face) ?? Code;
    }

    /// <summary>
    /// This block mirrored through the plane normal to <paramref name="axis"/>. Only a rot whose own axis IS that
    /// axis moves, and it moves to its opposite; run and face are invariant for the reason given above the section.
    /// </summary>
    public override AssetLocation GetHorizontallyFlippedBlockCode(EnumAxis axis)
    {
        string? rot = Variant["rot"], run = Variant["run"], face = Variant["face"];
        if (rot == null || run == null || face == null) return Code;

        BlockFacing? rotFacing = BlockFacing.FromCode(rot);
        if (rotFacing == null || rotFacing.Axis != axis) return Code;

        return CodeWithState(rotFacing.Opposite.Code, run, face) ?? Code;
    }

#if IW_DEBUG
    /// <summary>
    /// Prove E05's four acceptance criteria and the six transforms above by sweeping the whole registry, in one line
    /// at startup (T05.01 / T05.02). Called from the mod system beside the other self-checks; stripped entirely from
    /// a shipped build.
    ///
    /// <para><b>The oracle is the block REGISTRY plus an inline axis rule</b>, never the code under test. Legality is
    /// recomputed here from <c>BlockFacing.FromCode(rot).Axis</c> and a SECOND, local copy of the run → axis mapping,
    /// for the same reason <see cref="HalfLogPlacement.SelfCheck"/> keeps one: a check that asks
    /// <c>HalfLogPlacement.RunAxis</c> would agree with <see cref="StepRot"/> and <see cref="FlipRun"/> no matter what
    /// those did, since all three would be reading one function.
    ///
    /// <para>Probes go through the QUIET overload of <see cref="Get"/>: the public path writes a real
    /// <c>[Warning]</c> keyed by species, and a sweep of 1368 variants would fill the log with lines that look like
    /// genuine failures.
    /// </summary>
    internal static void WrenchSelfCheck(ICoreAPI api)
    {
        int variants = 0;
        int faceNotInvolution = 0, runNotInvolution = 0, rotNotInvolution = 0;
        int unregistered = 0, speciesLost = 0, illegalAxis = 0;
        int transformUnregistered = 0, transformSpeciesLost = 0, rotate360NotIdentity = 0, flipNotInvolution = 0;
        string firstBad = "";

        void Fail(ref int counter, string what)
        {
            counter++;
            if (firstBad.Length == 0) firstBad = what;
        }

        // The independent legality rule, restated on purpose (see the doc above).
        static bool Legal(string rot, string run)
        {
            BlockFacing? f = BlockFacing.FromCode(rot);
            if (f == null) return false;
            EnumAxis runAxis = run == "ns" ? EnumAxis.Z : run == "we" ? EnumAxis.X : EnumAxis.Y;
            return runAxis != f.Axis;
        }

        foreach (Block block in api.World.Blocks)
        {
            if (block is not BlockHalfLogSlab slab || slab.Code == null) continue;
            string? wood = slab.WoodCode;
            if (wood == null) continue;
            variants++;

            string rot = slab.Variant["rot"], run = slab.Variant["run"], face = slab.Variant["face"];
            BlockFacing rotFacing = BlockFacing.FromCode(rot);
            if (rotFacing == null) { Fail(ref illegalAxis, slab.Code + ": rot is not a facing"); continue; }

            // FACE (T05.02). One flip lands on a registered block of the same species, two flips are the identity.
            string flipped = face == BarkFace ? SplitFace : BarkFace;
            Block? faceStep = Get(api.World, wood, slab.SkinCode, rot, run, flipped, quiet: true);
            if (faceStep is not BlockHalfLogSlab faceSlab) Fail(ref unregistered, slab.Code + " -RMB-> " + flipped);
            else
            {
                if (faceSlab.WoodCode != wood) Fail(ref speciesLost, slab.Code + " -RMB-> " + faceSlab.Code);
                string back = flipped == BarkFace ? SplitFace : BarkFace;
                if (back != face) Fail(ref faceNotInvolution, slab.Code + " face flip is not its own inverse");
            }

            // RUN. Same three claims, plus the axis rule on the result.
            string? otherRun = FlipRun(rotFacing, run);
            if (otherRun == null || !Legal(rot, otherRun)) Fail(ref illegalAxis, slab.Code + " -LMB-> run " + (otherRun ?? "(none)"));
            else
            {
                Block? runStep = Get(api.World, wood, slab.SkinCode, rot, otherRun, face, quiet: true);
                if (runStep is not BlockHalfLogSlab runSlab) Fail(ref unregistered, slab.Code + " -LMB-> " + otherRun);
                else
                {
                    if (runSlab.WoodCode != wood) Fail(ref speciesLost, slab.Code + " -LMB-> " + runSlab.Code);
                    if (FlipRun(rotFacing, otherRun) != run) Fail(ref runNotInvolution, slab.Code + " run flip is not its own inverse");
                }
            }

            // ROT. Forward then backward is the identity, and both ends are legal and registered.
            (string fwdRot, string fwdRun) = StepRot(rotFacing, run, -1);
            if (!Legal(fwdRot, fwdRun)) Fail(ref illegalAxis, slab.Code + " -Shift+RMB-> " + fwdRot + "-" + fwdRun);
            else
            {
                Block? rotStep = Get(api.World, wood, slab.SkinCode, fwdRot, fwdRun, face, quiet: true);
                if (rotStep is not BlockHalfLogSlab rotSlab) Fail(ref unregistered, slab.Code + " -Shift+RMB-> " + fwdRot + "-" + fwdRun);
                else if (rotSlab.WoodCode != wood) Fail(ref speciesLost, slab.Code + " -Shift+RMB-> " + rotSlab.Code);

                BlockFacing fwdFacing = BlockFacing.FromCode(fwdRot);
                (string backRot, string backRun) = StepRot(fwdFacing, fwdRun, 1);
                if (backRot != rot || backRun != run)
                    Fail(ref rotNotInvolution, slab.Code + " -Shift+RMB-> " + fwdRot + "-" + fwdRun + " -Shift+LMB-> " + backRot + "-" + backRun);
            }

            // THE SIX TRANSFORMS. Every destination must resolve to a slab of the SAME species, 4 x 90 must be the
            // identity, and each flip must be its own inverse. Checked per variant rather than per species, because
            // the maps depend on rot and run, not on the species.
            AssetLocation?[] destinations =
            {
                slab.GetRotatedBlockCode(90), slab.GetRotatedBlockCode(180), slab.GetRotatedBlockCode(270),
                slab.GetVerticallyFlippedBlockCode(),
                slab.GetHorizontallyFlippedBlockCode(EnumAxis.X), slab.GetHorizontallyFlippedBlockCode(EnumAxis.Z),
            };
            foreach (AssetLocation? dest in destinations)
            {
                Block? destBlock = dest == null ? null : api.World.GetBlock(dest);
                if (destBlock is not BlockHalfLogSlab destSlab) { Fail(ref transformUnregistered, slab.Code + " -> " + (dest?.ToString() ?? "null")); continue; }
                if (destSlab.WoodCode != wood) Fail(ref transformSpeciesLost, slab.Code + " -> " + destSlab.Code);
                if (!Legal(destSlab.Variant["rot"], destSlab.Variant["run"])) Fail(ref illegalAxis, slab.Code + " -> " + destSlab.Code);
            }

            Block? spun = slab;
            for (int i = 0; i < 4; i++)
            {
                if (spun is not BlockHalfLogSlab step) { spun = null; break; }
                spun = api.World.GetBlock(step.GetRotatedBlockCode(90));
            }
            if (!ReferenceEquals(spun, slab)) Fail(ref rotate360NotIdentity, slab.Code + " does not come home after 4 x 90 degrees");

            if (api.World.GetBlock(slab.GetVerticallyFlippedBlockCode()) is BlockHalfLogSlab vflip
                && !ReferenceEquals(api.World.GetBlock(vflip.GetVerticallyFlippedBlockCode()), slab))
                Fail(ref flipNotInvolution, slab.Code + " vertical flip is not its own inverse");

            foreach (EnumAxis axis in new[] { EnumAxis.X, EnumAxis.Z })
            {
                if (api.World.GetBlock(slab.GetHorizontallyFlippedBlockCode(axis)) is BlockHalfLogSlab hflip
                    && !ReferenceEquals(api.World.GetBlock(hflip.GetHorizontallyFlippedBlockCode(axis)), slab))
                    Fail(ref flipNotInvolution, slab.Code + " horizontal flip about " + axis + " is not its own inverse");
            }
        }

        // Nothing registered at all: say so and stop, rather than printing a FAILED line with every defect counter at
        // zero — which is exactly what the walkStarts constant did before it was fixed, and exactly what the sibling
        // check does say (HalfLogPlacement.SelfCheck's "skipped: no half-log slab variant is registered").
        if (variants == 0)
        {
            api.Logger.Warning("[immersivewoodworking] Half-log slab wrench self-check skipped: no half-log slab variant is registered, so there is no state to turn.");
            return;
        }

        // THE WALK. From every one of the 12 legal (rot, run) pairs, stepping Shift+RMB must return to the start after
        // exactly 12 steps and must have visited 12 DISTINCT pairs on the way — i.e. one closed Hamiltonian cycle over
        // the legal pairs, which is the claim that makes "the wrench reaches every valid state" true. A shorter period
        // would mean the pairs split into separate orbits and some would be unreachable.
        //
        // Runs ONCE, not once per species, and that is a property of the walk rather than a saving: StepRot touches no
        // block and no species — it is string arithmetic over the (rot, run) grid, which is identical for all 57. The
        // REGISTRY side of the claim is carried by the per-variant probes above, which resolve every single step of
        // every variant through Get(). Do not "strengthen" this by feeding it a species; it would prove nothing more.
        const int LegalPairs = 12;
        int walkStarts = 0, walkWrongPeriod = 0, walkWrongCoverage = 0;

        foreach (BlockFacing startRot in BlockFacing.ALLFACES)
        {
            foreach (EnumAxis runAxis in AllAxes)
            {
                if (runAxis == startRot.Axis) continue;
                string startRun = HalfLogPlacement.RunCode(runAxis);
                walkStarts++;

                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                string rot = startRot.Code, run = startRun;
                int steps = 0;
                for (; steps < LegalPairs; steps++)
                {
                    seen.Add(rot + "-" + run);
                    (rot, run) = StepRot(BlockFacing.FromCode(rot), run, -1);
                    if (rot == startRot.Code && run == startRun) { steps++; break; }
                }

                if (steps != LegalPairs) walkWrongPeriod++;
                if (seen.Count != LegalPairs) walkWrongCoverage++;
            }
        }

        // DIRECTION ANCHORS. Everything above is symmetric: "4 x 90 is the identity", "each flip is its own inverse"
        // and "the walk closes after 12" would all hold just as well for an implementation that turned the wrong way.
        // Two literal expected values pin the sign, and they are the only hardcoded orientations in this check.
        //   Rotation: vanilla's own formula (HORIZONTALS_ANGLEORDER[Mod(HorizontalAngleIndex - quarters, 4)], the
        //   equivalent of BlockGroundAndSideAttachable.cs:169) sends north -> east at +90 degrees, and a Y rotation
        //   swaps a `we` log to `ns`. Turning the other way would send north -> west and pass every test above.
        //   Wrench: right mouse arrives as dir = -1 and must step north -> east, i.e. clockwise seen from above, the
        //   same sense BEConditional.cs:107 gives right mouse with GetCW().
        int directionWrong = 0;
        (string anchorRot, string anchorRun) = StepRot(BlockFacing.NORTH, "we", -1);
        if (anchorRot != "east") Fail(ref directionWrong, "Shift+RMB from north-we steps to " + anchorRot + "-" + anchorRun + ", expected east (clockwise)");

        foreach (Block block in api.World.Blocks)
        {
            if (block is not BlockHalfLogSlab probe || probe.Variant["rot"] != "north" || probe.Variant["run"] != "we") continue;
            if (api.World.GetBlock(probe.GetRotatedBlockCode(90)) is not BlockHalfLogSlab spun90)
            {
                Fail(ref directionWrong, probe.Code + " +90 degrees does not resolve");
            }
            else if (spun90.Variant["rot"] != "east" || spun90.Variant["run"] != "ns")
            {
                Fail(ref directionWrong,
                    probe.Code + " +90 degrees gives " + spun90.Variant["rot"] + "-" + spun90.Variant["run"] + ", expected east-ns");
            }
            break;   // one variant settles the sign; the grid is the same for every species
        }

        int broken = faceNotInvolution + runNotInvolution + rotNotInvolution + unregistered + speciesLost + illegalAxis
                   + transformUnregistered + transformSpeciesLost + rotate360NotIdentity + flipNotInvolution
                   + walkWrongPeriod + walkWrongCoverage + directionWrong;

        // walkStarts counts PAIRS, not states: `face` takes no part in the walk, so the sweep starts from 12 places
        // and the 24 states of one skin are 12 pairs x 2 faces. (This constant was 24 in the first draft and the check reported a
        // failure with every counter at zero — the reason it is spelled out here.)
        if (broken > 0 || walkStarts != LegalPairs)
        {
            api.Logger.Warning(
                "[immersivewoodworking] Half-log slab wrench self-check FAILED over {0} variants (first: {1}): {2} steps land on an unregistered state, {3} lose the species, {4} produce a log running along its own supporting face; face/run/rot flips not self-inverse {5}/{6}/{7}; transforms {8} unregistered, {9} species lost, {10} not identity after 360 degrees, {11} flips not self-inverse; the rot walk ran from {12} of {13} start pairs, {14} with the wrong period and {15} with the wrong coverage; {16} direction anchor(s) turn the wrong way.",
                variants, firstBad.Length == 0 ? "none" : firstBad, unregistered, speciesLost, illegalAxis,
                faceNotInvolution, runNotInvolution, rotNotInvolution,
                transformUnregistered, transformSpeciesLost, rotate360NotIdentity, flipNotInvolution,
                walkStarts, LegalPairs, walkWrongPeriod, walkWrongCoverage, directionWrong);
        }
        else
        {
            api.Logger.Notification(
                "[immersivewoodworking] Half-log slab wrench self-check: {0} variants, every gesture lands on a registered state of the same species; face and run flips are their own inverse, Shift+left undoes Shift+right, and the rot walk is one closed cycle of {1} pairs from all {2} starts (so {3} states per species and skin are reachable). World edit and schematics: 3 rotations and 3 mirrors per variant all resolve, 4 x 90 degrees is the identity and both mirrors are their own inverse. Direction anchors hold: Shift+right steps north to east (clockwise) and +90 degrees sends north-we to east-ns.",
                variants, LegalPairs, walkStarts, LegalPairs * 2);
        }
    }
#endif
}

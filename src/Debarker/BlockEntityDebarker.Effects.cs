using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// The debarker's client effects (AutoDebarkerPlan E07). T07.05: bark chips while debarking, a steady stream on a copy
/// of the sawhorse's BarkChips template (no collision, no wind, a computed life). Only the client emits it, from
/// <c>AdvanceClientProgress</c> once per Opaque frame before visibility culling; the server spawns none.
///
/// T07.06: the sound, two temporary layers the author chose on 2026-11-24. The mechanism bed is the chopper's loop,
/// driven by the shared <see cref="LoopSound"/> from a 100 ms client tick while the reducer and the rollers turn
/// (<see cref="EffectiveDebarkSpeed"/> &gt; 0: an assembled machine on a running line), log or no log. The
/// scraping is the sawhorse's three hand-debarking clips, one after another while a log is being stripped, driven every
/// frame by the same <c>AdvanceClientProgress</c> check the chips take. No worm-gear clacks: a worm meshes with its wheel
/// continuously, with no tooth strikes to hear. Both layers are freed from <c>DisposeRenderer</c> (Render partial), which
/// owns the removal and unload overrides.
/// </summary>
public partial class BlockEntityDebarker
{
    // The chip and sound numbers in this file were accepted by the author in game at T07.07 (2026-11-25), the chips on
    // birch and oak, at 60 fps and under a 30 fps limiter.

    // At network speed 1 the knives shed as many flakes as one sawhorse emitter.
    const float ChipsPerSecondAtUnitSpeed = 36f;
    const float MaxChipsPerSecond = 72f;
    const int MaxChipsPerFrame = 8; // A stalled frame must not replay its whole backlog.

    // A chip spawned inside the log is never seen. The rotating bark reaches x ≈ 1.385 px
    // (-4 + sqrt(5² + 2²)); the knives span x = 1..2 px and the disc plate starts at x = 2 px.
    const float ChipX = 1.75f / 16f, ChipY = 34f / 16f;
    const float ChipZHalfSpread = 7f / 16f;
    const float ChipVelocityX = -0.12f, ChipVelocityY = -1f; // Down towards the chute.
    const float ChipVelocitySpread = 0.08f;
    // From y = 34 px with vy = -1 block/s and gravity 1 × 0.3 × 40 = 12 blocks/s², the last drawn quad lies in the
    // chute opening (y = 1.5..10.5 px) at a steady frame time; AutoDebarkerPlan/check_debarker_chip_life.py checks it.
    // Near 30 fps a step gets either none or one in-between frame, and a life fixed at the spawn cannot follow that:
    // under a 30 fps limiter about half the chips end outside the opening, which the author accepted. Recheck if any of
    // these change.
    const float ChipFallSeconds = 0.48f;
    const float ParticleStepSeconds = 0.033f; // ParticlePhysics.PhysicsTickTime in the main-thread pool.
    // About how far back the in-between age per physics step is averaged.
    const float FrameSmoothingSeconds = 0.75f;

    // Size 0.15..0.4 and gravity 1 are the sawhorse's; the gravity enters ChipFallSeconds above. Quantity, colour,
    // position, velocity and life are set on every spawn.
    static readonly SimpleParticleProperties BarkChips = new(
        0, 0, 0, new Vec3d(), new Vec3d(), new Vec3f(), new Vec3f(),
        0f, 1f, 0.15f, 0.4f, EnumParticleModel.Quad)
    {
        // Author decision 2026-11-22: chips pass through the machine's full cubes and expire at the chute.
        WithTerrainCollision = false,
        // The sawhorse's 0.3 would carry the stream sideways out of the chute opening. The engine reads only this field;
        // WindAffected is never consulted.
        WindAffectednes = 0f
    };

    float chipRemainder;

    // A shadow of the main-thread pool's step clock (ParticleBase.TickFixedStep). A frame that reaches the step closes
    // it and drops the excess; the frames before it age every chip by their dt without a physics step, so before the
    // reset the accumulator holds exactly that in-between age. chipIntervalAge is its smoothed value per step, the term
    // a chip's life must allow for. A mean frame time misjudges it on uneven frames (vsync falling to half rate), where
    // a long frame closes a step on its own. -1 until the first step is seen.
    float chipStepAccum, chipIntervalAge = -1f;

    void SampleChipFrameTime(float dt)
    {
        float inBetween = chipStepAccum;
        chipStepAccum += dt;
        if (chipStepAccum < ParticleStepSeconds) return;
        chipStepAccum = 0f;
        // One weight per step, not per second: weighting by the frames' length overrates the steps that had any.
        chipIntervalAge = chipIntervalAge < 0f
            ? inBetween
            : chipIntervalAge + (inBetween - chipIntervalAge) * (ParticleStepSeconds / FrameSmoothingSeconds);
    }

    /// <summary>Emits this working frame's share of bark chips along the knife line.</summary>
    void SpawnBarkChips(ICoreClientAPI client, float dt)
    {
        float rate = Math.Min(NetworkSpeed * ChipsPerSecondAtUnitSpeed, MaxChipsPerSecond);
        chipRemainder = Math.Min(chipRemainder + rate * dt, MaxChipsPerFrame);
        int count = (int)chipRemainder;
        chipRemainder -= count;
        if (count == 0 || barkTexture == null) return;
        int color = client.BlockTextureAtlas.GetRandomColor(barkTexture, -1);
        if (color == 0) return;

        // MulWithVec3 drops facingMat's translation, so the offsets are taken from the block centre and turned about it,
        // and the velocity turns as a direction.
        var offset = new FastVec3f(ChipX - 0.5f, ChipY - 0.5f, 0f);
        var spread = new FastVec3f(0f, 0f, ChipZHalfSpread);
        var velocity = new FastVec3f(ChipVelocityX, ChipVelocityY, 0f);
        if (facingMat != null)
        {
            offset = Mat4f.MulWithVec3(facingMat, offset.X, offset.Y, offset.Z);
            spread = Mat4f.MulWithVec3(facingMat, spread.X, spread.Y, spread.Z);
            velocity = Mat4f.MulWithVec3(facingMat, velocity.X, velocity.Y, velocity.Z);
        }
        float halfX = Math.Abs(spread.X), halfZ = Math.Abs(spread.Z);
        BarkChips.MinPos.Set(Pos.X + 0.5 + offset.X - halfX, Pos.InternalY + 0.5 + offset.Y, Pos.Z + 0.5 + offset.Z - halfZ);
        BarkChips.AddPos.Set(2f * halfX, 0, 2f * halfZ);
        BarkChips.MinVelocity.Set(velocity.X - ChipVelocitySpread, velocity.Y - ChipVelocitySpread, velocity.Z - ChipVelocitySpread);
        BarkChips.AddVelocity.Set(2f * ChipVelocitySpread, 2f * ChipVelocitySpread, 2f * ChipVelocitySpread);
        BarkChips.Color = color | (255 << 24);
        BarkChips.MinQuantity = count;
        // TickFixedStep ages the chip between physics steps. Spawned primes the first step, so only the intervals after
        // that step accumulate in-between frames: after n steps the age is n·step + (n−1)·intervalAge.
        float intervalAge = Math.Max(chipIntervalAge, 0f);
        BarkChips.LifeLength = ParticleLife.Seconds(ChipFallSeconds * (1f + intervalAge / ParticleStepSeconds) - intervalAge);
        client.World.SpawnParticles(BarkChips);
    }

    // The machine has no recordings of its own yet; they would go to sounds/debarker/ and replace these paths. All mono,
    // or OpenAL would not place them (memory vs-positional-audio-mono).
    static readonly AssetLocation SndBed = new("immersivewoodworking", "sounds/dropmaul/ambient");
    static readonly AssetLocation[] SndScrape =
    {
        new("immersivewoodworking", "sounds/debark/debarking1"),
        new("immersivewoodworking", "sounds/debark/debarking2"),
        new("immersivewoodworking", "sounds/debark/debarking3"),
    };

    // The chopper's level, range and fade for the bed, the sawhorse's for the clips and their release. Under the
    // Spatial Audio mod the author heard the scraping quieter in front of the machine than behind it. By that mod's
    // decompiled code (0.2.1) its walls are every cell's collision boxes, and the knife line sits inside a box of
    // (0,2,0); not confirmed in game. The author's call at T07.07 (2026-11-25): left as it is.
    const float BedVolume = 0.168f, BedRange = 24f, BedFadeSeconds = 0.4f;
    const float ScrapeVolume = 1f, ScrapeRange = 16f, ScrapeFadeSeconds = 0.3f;
    // The wait after a clip came back already disposed (no free OpenAL source), as long as LoopSound's.
    const float ScrapeRetrySeconds = 5f;

    // The fade edges, the earshot gate, the slot freed after 10 s of idle and the latch on a load failure are LoopSound's.
    readonly LoopSound bedLoop = new(SndBed, BedVolume, BedRange, BedFadeSeconds);

    // The clip that is playing. A ShouldLoop buffer would repeat one clip, so each clip plays once and the next random
    // one follows it, as on the sawhorse. DisposeOnFinish is off: this code frees every clip itself.
    ILoadedSound? scrapeClip;
    // Seconds before the next load; +∞ after a missing asset, which cannot appear within a session.
    float scrapeRetryIn;

    /// <summary>The bed's 100 ms client tick, registered in <see cref="InitRenderer"/>. The loop plays while
    /// <see cref="EffectiveDebarkSpeed"/> &gt; 0, the gate the reducer and the rollers move on. Through a singleplayer
    /// pause it plays on, as the chopper's and the sawmill's do: the tick stands, and the engine pauses no sound by
    /// itself.</summary>
    void OnBedSoundTick(float dt)
    {
        if (capi == null) return;
        // On the shaft in the master's centre column, which the facing turn leaves in place. Plain Pos.Y, the space
        // LoopSound reads the listener in, as for the chopper's and the sawmill's loops; the engine's listener is at
        // InternalY (SystemSoundEngine.OnRenderFrame), the same value in dimension 0.
        bedLoop.Update(capi, dt, EffectiveDebarkSpeed() > 0f, new Vec3d(Pos.X + 0.5, Pos.Y + 1.5, Pos.Z + 0.5));
    }

    /// <summary>
    /// Drives the scraping for one frame from <c>AdvanceClientProgress</c>, with that frame's <see cref="CanDebark"/> as
    /// <paramref name="scraping"/>. While it holds, a clip that has ended is freed and the next random one starts at the
    /// knife line, at most a frame later; when it fails, the clip that is playing fades out. No clip starts farther from
    /// the listener than <see cref="ScrapeRange"/>, the distance past which <c>PlaySoundAt</c> skips a one-shot. Client
    /// only, on the main thread, where <c>LoadSound</c> insists on running.
    /// </summary>
    void UpdateScrapeSound(ICoreClientAPI client, float dt, bool scraping)
    {
        if (scrapeRetryIn > 0f) scrapeRetryIn -= dt;
        if (!scraping) { FadeOutScrapeClip(); return; }

        // Wait while the clip plays or has no OpenAL source yet (SourcePending). HasStopped reads a missing source as
        // stopped, and a new load every frame of the decode would leave a chain of revived clips with no owner.
        ILoadedSound? clip = scrapeClip;
        if (clip != null && !clip.IsDisposed && (SourcePending(clip) || !clip.HasStopped)) return;
        clip?.Dispose(); // Stop does not return the slot (memory vs-loadedsound-slot-freed-only-by-dispose)
        scrapeClip = null;
        if (scrapeRetryIn > 0f) return;

        Vec3d at = KnifeLineWorld();
        EntityPlayer? listener = client.World.Player?.Entity;
        if (listener != null && listener.Pos.SquareDistanceTo(at) > ScrapeRange * ScrapeRange) return;

        clip = client.World.LoadSound(new SoundParams
        {
            Location = SndScrape[client.World.Rand.Next(SndScrape.Length)],
            ShouldLoop = false,
            DisposeOnFinish = false,
            RelativePosition = false,
            Position = new Vec3f((float)at.X, (float)at.Y, (float)at.Z),
            Volume = ScrapeVolume,
            Range = ScrapeRange,
        });
        if (clip == null) { scrapeRetryIn = float.PositiveInfinity; return; } // missing asset; LoadSound has logged it
        if (clip.IsDisposed) { scrapeRetryIn = ScrapeRetrySeconds; return; }
        clip.Start();
        scrapeClip = clip;
    }

    /// <summary>Fades the clip that is playing over <see cref="ScrapeFadeSeconds"/> and frees it afterwards, as the
    /// sawhorse releases its strip sound; the engine runs the callback on the main thread. Detached first, so a restart
    /// meanwhile starts a fresh clip over the fading tail. A clip still without its source stays attached: once built it
    /// starts, and the next frame with the gate shut fades it like any other.</summary>
    void FadeOutScrapeClip()
    {
        ILoadedSound? clip = scrapeClip;
        if (clip == null || (!clip.IsDisposed && SourcePending(clip))) return;
        scrapeClip = null;
        if (clip.IsDisposed) return;
        if (clip.IsPlaying) clip.FadeOut(ScrapeFadeSeconds, s => s.Dispose());
        else clip.Dispose();
    }

    /// <summary>
    /// True while the engine has not built the clip's OpenAL source. When <c>PlaySoundAt</c> is still decoding the
    /// sample (its load is asynchronous), <c>LoadSound</c> returns a handle whose source the engine builds later on the
    /// main thread, and only that build sets <see cref="ILoadedSound.SoundLengthSeconds"/>. The build does not check
    /// whether the handle was disposed meanwhile: it clears the flag, makes a source and plays it if <c>Start</c> was
    /// called (<c>LoadedSoundNative.createSoundSource</c>, VS 1.22), and nothing frees that source again. So such a handle
    /// is kept, not disposed. Teardown cannot wait for it (<see cref="DisposeSound"/>); a teardown inside the few
    /// milliseconds of a first decode leaves one silent source behind.
    /// </summary>
    static bool SourcePending(ILoadedSound clip) => clip.SoundLengthSeconds <= 0f;

    /// <summary>The middle of the knife line, where the chips leave the log (<see cref="ChipX"/>, <see cref="ChipY"/>),
    /// with the dimension offset the listener carries.</summary>
    Vec3d KnifeLineWorld()
    {
        var offset = new FastVec3f(ChipX - 0.5f, ChipY - 0.5f, 0f);
        if (facingMat != null) offset = Mat4f.MulWithVec3(facingMat, offset.X, offset.Y, offset.Z);
        return new Vec3d(Pos.X + 0.5 + offset.X, Pos.InternalY + 0.5 + offset.Y, Pos.Z + 0.5 + offset.Z);
    }

    /// <summary>Stops and frees both layers immediately, from <c>DisposeRenderer</c> on removal and unload: no fade, the
    /// block entity is going away, and only Dispose gives a slot back. A clip already fading frees itself. The Stop keeps
    /// a clip whose source is still pending silent when the engine builds it (<see cref="SourcePending"/>).</summary>
    void DisposeSound()
    {
        bedLoop.Dispose();
        if (scrapeClip is { IsDisposed: false } clip) { clip.Stop(); clip.Dispose(); }
        scrapeClip = null;
    }
}

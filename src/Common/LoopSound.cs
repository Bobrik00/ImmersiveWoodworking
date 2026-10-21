using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// AuditFixPlan T06.03 / F-L10-08 (+ F-L10-14, F-L2-26) — one owned, gated, releasable looping sound. Both powered
/// machines drive their ambience through this, so the lifecycle rule exists once instead of drifting into two
/// versions, exactly like <see cref="MachineRenderGate"/> holds the one per-frame visibility rule.
///
/// <para><b>Why the slot matters.</b> The client's concurrency budget is <c>ClientMain.ActiveSounds</c>, and the
/// "max concurrent sounds of 250 reached" check sits ONLY on the one-shot path (<c>PlaySoundAtInternal</c>);
/// <c>LoadSound</c> enqueues without any cap. A sound leaves that queue on exactly one condition, swept every
/// 500 ms: <c>Params.DisposeOnFinish &amp;&amp; HasStopped</c>, or the handle being disposed. Our loops are
/// <c>DisposeOnFinish = false</c> by design (we own them, so the engine must not free them mid-fade), which means
/// <c>Stop()</c> does NOT return the slot — only <c>Dispose()</c> does. A machine that ran once therefore held its
/// slot until the chunk unloaded, and the victims were never the loops: they were every one-shot in the game,
/// footsteps and block breaks and this mod's own taps and clacks, silently refused once a base of machines had
/// eaten the budget. Verified by decompiling <c>ClientMain</c> + <c>SystemSoundEngine</c> (VS 1.22).</para>
///
/// <para><b>Release with hysteresis, not on every stop.</b> Disposing on the falling edge would return the slot at
/// once but pay a fresh <c>LoadSound</c> (a new OpenAL source, buffer and a full PCM upload) on every restart, and
/// these gates flicker — a mechanical network dipping through zero speed stops and starts the loop in the same
/// second. So a stop keeps the handle and only <see cref="IdleReleaseSeconds"/> of CONTINUOUS idle frees it: rapid
/// restarts stay on the cheap path, a machine genuinely standing still gives the slot back.</para>
///
/// <para><b>Earshot gate.</b> The loops are driven from a 100 ms block-entity tick, which runs for every loaded
/// chunk out to the client's view distance — hundreds of blocks. Past <c>range</c> the engine's own attenuation is
/// already down to 1% gain (<c>createSoundSource</c> solves the rolloff for exactly that), so a loop out there is
/// inaudible and still costs a slot. It is not created past <c>range</c> and is released past
/// <see cref="FarReleaseFactor"/> × range; the gap between the two thresholds is what keeps a player pacing the
/// boundary from reloading the sample every step. This is a SOUND gate and does not reopen the T05.06 decision
/// against a distance cut in the renderers: that decision rests on the static frame living in the chunk mesh and
/// having to keep drawing, which has no audio counterpart.</para>
///
/// <para><b>Resume inside a fade-out (F-L2-26).</b> Both machines used to ask <c>if (!snd.IsPlaying)</c> before
/// (re)starting. <c>IsPlaying</c> reads the live OpenAL source state, and <c>FadeOutAndStop</c> ramps the volume on
/// a pool thread WITHOUT stopping the source — so through the whole fade the answer is "yes, playing", the restart
/// was skipped, and the pending <c>Stop()</c> callback fired anyway. A gate that flickered faster than the fade
/// therefore dropped the ambience into silence for the fade plus a tick and then faded it back in from zero.
/// Here the desired state is OUR flag (<see cref="started"/>), so a resume always acts; it re-targets the fade with
/// <c>FadeTo</c> from whatever volume the fade-out reached, which the API documents as cancelling the earlier
/// fade's completion callback — the pending stop — and which costs no restart of the sample.</para>
///
/// <para><b>Load failure (F-L10-14).</b> <c>ClientMain.LoadSound</c> logs "Audio File not found" and returns null
/// for a missing asset, so an unlatched retry on a 100 ms tick is ten warnings a second, forever, per machine. A
/// missing asset cannot appear mid-session, so that latches permanently (a chunk reload builds a new block entity
/// and clears it). The other failure is a handle that arrives already disposed — <c>createSoundSource</c> swallows
/// its own exception, so an OpenAL source-exhausted client hands back a live-looking dead object — and THAT is
/// transient, so it only backs off for <see cref="RetryAfterSeconds"/>. Releasing handles makes this path more
/// reachable than it was, which is why it is handled rather than left to chance.</para>
/// </summary>
internal sealed class LoopSound
{
    /// <summary>Continuous idle after which the handle is freed and the slot returned. Long enough that the
    /// stop/start flicker of a mechanical network never reloads the sample, short enough that a shut-down machine
    /// is not still holding budget when the player walks away.</summary>
    const float IdleReleaseSeconds = 10f;

    /// <summary>Release distance as a multiple of the audible <c>range</c>. The band between the two is the
    /// hysteresis that stops a player standing on the threshold from thrashing load/dispose.</summary>
    const float FarReleaseFactor = 1.25f;

    /// <summary>Back-off after a transient load failure (a handle that came back already disposed).</summary>
    const float RetryAfterSeconds = 5f;

    readonly AssetLocation location;
    readonly float volume;
    readonly float range;

    /// <summary>Fade length for start and stop, or 0 for a hard <c>Start</c>/<c>Stop</c>. The chopper's ambience
    /// fades (a bare mechanism bed pops audibly); the sawmill's slide and squeak do not.</summary>
    readonly float fadeSeconds;

    ILoadedSound? snd;

    /// <summary>Our belief about the loop: Start issued, Stop not yet. The desired state lives here and not in
    /// <c>ILoadedSound.IsPlaying</c> — see the F-L2-26 paragraph above.</summary>
    bool started;

    /// <summary>Seconds of continuous idle since the falling edge, counted only while stopped.</summary>
    float idleSeconds;

    /// <summary>Seconds to wait before attempting a load again; <see cref="float.PositiveInfinity"/> after a
    /// missing asset, which never becomes loadable within a session.</summary>
    float retryIn;

    internal LoopSound(AssetLocation location, float volume, float range, float fadeSeconds = 0f)
    {
        this.location = location;
        this.volume = volume;
        this.range = range;
        this.fadeSeconds = fadeSeconds;
    }

    /// <summary>Drive the loop for one client tick. <paramref name="capi"/> null (the dedicated server) is a no-op:
    /// nothing was ever loaded there. <paramref name="dt"/> is the tick's seconds, <paramref name="shouldPlay"/> the
    /// machine's own gate, <paramref name="at"/> the world anchor to sound from.
    ///
    /// Must be called from the main thread — <c>LoadSound</c> throws otherwise, by design — which the 100 ms block
    /// entity tick both callers use satisfies.</summary>
    internal void Update(ICoreClientAPI? capi, float dt, bool shouldPlay, Vec3d at)
    {
        if (capi == null) return;

        // The engine can dispose a handle behind our back (a failed source rebuild on an output-device change), so
        // never carry a dead one: drop it and let the normal path decide whether to load a replacement.
        if (snd != null && snd.IsDisposed) { snd = null; started = false; }
        if (retryIn > 0f) retryIn -= dt;

        if (!shouldPlay)
        {
            if (started) StopNow();          // falling edge, once — not every tick
            if (snd == null) return;         // already released (or never loaded): nothing left to count down to
            idleSeconds += dt;
            if (idleSeconds >= IdleReleaseSeconds) Release();
            return;
        }

        idleSeconds = 0f;

        float d2 = ListenerDistanceSquared(capi, at);
        if (d2 > range * range * FarReleaseFactor * FarReleaseFactor) { Release(); return; } // running, nobody hears it
        if (snd == null && d2 > range * range) return;                                       // in the hysteresis band

        if (snd == null)
        {
            if (retryIn > 0f) return; // includes the permanent latch: infinity never decays past zero
            snd = capi.World.LoadSound(new SoundParams
            {
                Location = location,
                ShouldLoop = true,
                DisposeOnFinish = false, // a looping sound we own — the engine must not free it mid-fade
                RelativePosition = false,
                Position = new Vec3f((float)at.X, (float)at.Y, (float)at.Z),
                Volume = volume,
                Range = range,
            });
            if (snd == null) { retryIn = float.PositiveInfinity; return; }  // asset missing — permanent
            if (snd.IsDisposed) { snd = null; retryIn = RetryAfterSeconds; return; } // source creation failed — transient
        }
        else if (!started)
        {
            // A handle built before the mover meshes existed anchored on the fallback machine centre; a restart is
            // the natural moment to move it onto the real part pivot. One OpenAL call, on the rising edge only.
            snd.SetPosition((float)at.X, (float)at.Y, (float)at.Z);
        }

        if (started) return;
        started = true;

        if (fadeSeconds <= 0f)
        {
            if (!snd.IsPlaying) snd.Start();
            snd.SetVolume(volume);
            return;
        }

        if (snd.IsPlaying)
        {
            // Mid fade-out: re-target the ramp instead of restarting the source. This both cancels the pending stop
            // (the API documents a later FadeXXX as suppressing the earlier one's callback) and avoids the click a
            // second Start would make by rewinding a sample that is still sounding.
            snd.FadeTo(volume, fadeSeconds, null);
            return;
        }

        // Fade in from silence so the loop doesn't pop on at full volume. FadeTo's target is exactly the value we
        // pass, unlike FadeIn, whose target-volume convention is ambiguous across the engine's own call sites.
        snd.Start();
        snd.SetVolume(0f);
        snd.FadeTo(volume, fadeSeconds, null);
    }

    /// <summary>Hard stop and free on teardown (block unloaded or removed): no fade, the block entity is going away
    /// and a tail would be cut by the dispose anyway. Idempotent, and a no-op on the server / for a loop that never
    /// started.</summary>
    internal void Dispose()
    {
        if (snd != null && snd.IsPlaying) snd.Stop();
        Release();
    }

    /// <summary>Silence the loop but keep the handle, so a restart within <see cref="IdleReleaseSeconds"/> costs
    /// nothing.</summary>
    void StopNow()
    {
        started = false;
        idleSeconds = 0f;
        if (snd == null || snd.IsDisposed) return;
        if (fadeSeconds > 0f) snd.FadeOutAndStop(fadeSeconds);
        else if (snd.IsPlaying) snd.Stop();
    }

    /// <summary>Free the handle. This — not <c>Stop</c> — is what hands the slot back: the engine's 500 ms sweep
    /// drops a disposed sound from <c>ActiveSounds</c> on its next pass.</summary>
    void Release()
    {
        started = false;
        idleSeconds = 0f;
        snd?.Dispose();
        snd = null;
    }

    /// <summary>Squared distance from the listener to the anchor. Read with the player entity's plain
    /// <c>Pos</c>, the same space the anchor is built in (block <c>Pos</c> plus a part pivot), so the gate and the
    /// spatialization can never disagree about where the machine is. The camera, not the player, is the true
    /// listener, but the two differ by a couple of blocks in third person and the thresholds are 24 and 30.
    /// Before the player entity exists (very early on a world join) the loop is treated as audible: the ordinary
    /// gates still decide, and one tick of grace costs nothing.</summary>
    static float ListenerDistanceSquared(ICoreClientAPI capi, Vec3d at)
    {
        EntityPlayer? e = capi.World.Player?.Entity;
        if (e == null) return 0f;
        return at.SquareDistanceTo(e.Pos.X, e.Pos.Y, e.Pos.Z);
    }
}

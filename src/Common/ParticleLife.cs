namespace ImmersiveWoodworking;

/// <summary>
/// The one place that knows what a particle template's <c>LifeLength</c> actually means (AuditFixPlan T06.02 /
/// F-L10-07).
///
/// <para>Why this exists: the field is NOT seconds, and every comment in this mod used to say it was. The engine
/// stamps a spawning particle with <c>LifeLength = props.LifeLength * (5f / sqrt(currentGamespeed))</c>
/// (<c>ParticlePoolQuads.SpawnParticles</c>; <c>ParticlePoolCubes</c> inherits it, so both models are affected),
/// and <c>currentGamespeed = game.Calendar.SpeedOfTime / 60f</c>, which is exactly 1 while time runs normally. So
/// an authored 1.0 lives FIVE seconds on screen, and the numbers this mod tuned in-game were five times what their
/// comments claimed: sawhorse dust 4 s for "0.8", sawmill dust 10 s for "2", bark and axe chips 5 s for "1".</para>
///
/// <para>The fix is to author in real seconds and convert here, so a future tuning session reads the number it
/// will actually see. <see cref="Seconds"/> is the only way a template should get its life.</para>
///
/// <para>The sibling trap, for whoever reads this next: the same method scales COUNT the other way, by
/// <c>Quantity * currentGamespeed</c>. So a template that asks for an exact count gets exactly that at normal
/// speed and proportionally more under accelerated time. Nothing in this mod depends on the difference, but a
/// comment claiming "an exact count" is only true at speed 1.</para>
///
/// <para>The multiplier is fixed at 5 rather than read from the calendar because a template is a static field,
/// built once at class-init, while <c>currentGamespeed</c> is live: under accelerated time (sleeping, <c>/time
/// speed</c>) the engine's own divisor shortens every particle in the game, ours included, and that is the
/// engine's business. What we author is the picture at normal speed, which is the picture the author tunes
/// against.</para>
///
/// <para>Author decision 2026-08-28, with the numbers this class now carries: the real lifetimes were cut to a
/// THIRD of what they were, not to a fifth. Cutting to a fifth is what "make the value honest" would have meant —
/// leave the constants alone and only fix the comments — and it would have thrown away a visual the author had
/// already approved live. A third keeps the flakes on screen long enough to read as settling debris while ending
/// the long motionless tail: a flake with gravity and <c>WithTerrainCollision</c> lands in well under a second and
/// then runs terrain physics 30 times a second for the whole remainder. For scale, vanilla's own tree-felling dust
/// (<c>ItemAxe.dustParticles</c>, <c>LifeLength = 4f</c>) lives 20 s, so even the old numbers were frugal by
/// comparison.</para>
/// </summary>
internal static class ParticleLife
{
    /// <summary>What the engine multiplies an authored <c>LifeLength</c> by at spawn, at normal time speed.</summary>
    internal const float EngineMultiplier = 5f;

    /// <summary>The value to hand a particle template so its flakes live <paramref name="seconds"/> REAL seconds
    /// on screen at normal time speed.</summary>
    internal static float Seconds(float seconds) => seconds / EngineMultiplier;
}

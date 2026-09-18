namespace CYAC.Port.Core.Sim.Combat.Effects;

/// <summary>
/// What the renderer needs to know about ONE live smoke puff to size and colour it: the slot's
/// kind byte and the puff's age against its span, both in the original's frame-time units (<c>256
/// = one unit of g_master_frame_counter [0xF0C8]</c>, i.e. about one second).  The
/// size-and-colour LAW itself — the port of the class's prepare callback — is presentation and
/// lives in <c>CYAC.Port.Render.SmokeLook</c>; this type is the SIM's half: the slot walk and the
/// age arithmetic, so the renderer is handed three numbers and never a pool pointer.
/// </summary>
/// <param name="Kind">
/// The slot's <c>+0x08</c> type byte (<c>s_smoke_slot</c>): 0 = a stationary puff, 1 = the DAMAGE
/// trail <c>weapon_fire_combat_loop</c> attaches to a hit aeroplane, 3 = the WRECK column, 2/4 =
/// the wide white variants; ≥ 5 keeps the static look.
/// </param>
/// <param name="AgeFrameTime">
/// <c>g_frame_time_accum_lo [0xF0D2] − (slot[+4] &amp; 0xFF) &lt;&lt; 8</c> as a signed word
/// (<c>image@0x0B328..0x0B331</c>): how long ago the puff's birth FRAME began.
/// </param>
/// <param name="SpanFrameTime">
/// <c>((slot[+6] − slot[+4]) &amp; 0xFF) &lt;&lt; 8</c> (<c>image@0x0B320..0x0B326</c>): the
/// puff's whole life — 45, 25 or 5 frame units for kinds 0, 3 and the rest
/// (<see cref="SmokePuffTable.Lifetime"/>).
/// </param>
public readonly record struct SmokePuffState(byte Kind, int AgeFrameTime, int SpanFrameTime)
{
    /// <summary>The age as a fraction of the span, unclamped (the birth frame's fraction can push it a little past 1).</summary>
    public double LifeFraction => SpanFrameTime > 0 ? (double)AgeFrameTime / SpanFrameTime : 0.0;

    /// <summary>
    /// Finds the smoke slot whose pool object is <paramref name="objectRef"/> — the prepare
    /// callback's own walk from <c>[0xB930]</c> down to <c>[0xB8B2]</c> comparing <c>slot[+0]</c>
    /// with <c>g_render_current_object_id</c> (<c>smoke_sprite_render_params_setup
    /// @image@0x0B307..0x0B31E</c>) — and reads its state as the callback does
    /// (<c>image@0x0B320..0x0B331</c>).
    /// </summary>
    /// <param name="registers">The register file.</param>
    /// <param name="objectRef">The pool object being drawn.</param>
    /// <returns>The puff's state, or null when no slot names the object.</returns>
    public static SmokePuffState? Read(CombatRegisters registers, ushort objectRef)
    {
        ArgumentNullException.ThrowIfNull(registers);
        if (objectRef == 0)
        {
            return null;
        }

        foreach (int slot in SmokePuffTable.Slots())
        {
            if (registers.Word(slot) != objectRef)
            {
                continue;
            }

            int birthLo = registers.Word(slot + 4) & 0xFF;                  // image@0x0B328 mov ah,[si+4]
            int expiryLo = registers.Word(slot + 6) & 0xFF;                 // image@0x0B320 mov bh,[si+6]
            int span = ((expiryLo - birthLo) & 0xFF) << 8;                  // image@0x0B323..0x0B326
            int accumLo = registers.Word(DeferredEffectPool.FrameTimeAccumulator);
            int age = unchecked((short)(ushort)(accumLo - (birthLo << 8))); // image@0x0B32D..0x0B331
            return new SmokePuffState(registers.Byte(slot + 8), age, span);
        }

        return null;
    }
}

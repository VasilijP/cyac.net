using CYAC.Port.Render.Cockpit;

namespace CYAC.Port.Host.Sim;

/// <summary>
/// Which of the three doors an advisory came through — the shipped code has one message dispatcher
/// and two wrappers, and they gate differently.
/// </summary>
public enum AdvisorDispatchKind
{
    /// <summary>
    /// <c>ai_advisor_message_dispatch @image@0x0EFF1</c> called directly.  Nine sites; the five the
    /// port's integer kernels reach are codes 0/1 (a kill, <c>image@0x0BFCA</c>), 2 (the abort
    /// deadline, <c>image@0x0FE37</c>), 3 (a shot with no lock, <c>image@0x034AD</c>) and 4/7/8
    /// (a bandit or a missile behind you, <c>image@0x0825A</c>).
    /// </summary>
    Direct,

    /// <summary>
    /// <c>ai_advisor_check_then_dispatch @image@0x0F333</c> — fires ONCE per sortie per code: the
    /// action's bit must still be clear in <c>g_advisor_active_bitmask [0xBCFA]/[0xBCFC]</c>.  Two
    /// sites: code 18 from the mission module's win hook (<c>image@0x08C69</c>) and code 20 from the
    /// damage rule (<c>image@0x0F3F0</c>).
    /// </summary>
    OneShot,

    /// <summary>
    /// <c>ai_advisor_repeated_dispatch @image@0x0F35B</c> — first fire free, then throttled by
    /// <see cref="AdvisorTiming.RepeatThrottleUnits"/> of <c>g_master_frame_counter [0xF0C8]</c> per
    /// code (<c>g_advisor_per_event_stamp [0xBCC6]</c>).  Seven sites, of which the port reaches
    /// codes 15/16/17 through the flight kernel (<c>image@0x2B44C</c>).
    /// </summary>
    RateLimited,
}

/// <summary>
/// The YEAGER advisor's message law, port-side.
/// </summary>
/// <remarks>
/// <para>
/// The integer kernels ALREADY raise the advisories — <c>IEngagementNodeEvents.AdvisorMessage</c>,
/// <c>ILifecycleEvents.AdvisorAction</c> and <c>IKernelWorld.DispatchFlightAdvisor</c> — and until
/// all three used only to count.  This class is what they now feed: a PRESENTATION-side queue
/// that runs <c>ai_advisor_message_dispatch @image@0x0EFF1</c>'s own law (the cooldown, the one-shot
/// bitmask, the code-2 bypass, the code-0 random gate, the two timestamps and the display type) and
/// publishes an <see cref="AdvisorPanelState"/> for the window to draw.
/// </para>
/// <para>
/// <b>Byte-inert by construction.</b> Nothing here writes simulation state; the only random draw is
/// the kill advisory's, and that site (<c>image@0x0F03F</c>) is assigned to the <c>Fx.Indicators</c>
/// stream by the project's own census, so it cannot reach a decision.  A raise that arrives inside a
/// step is BUFFERED and pumped once per presented frame, which is also what keeps the class
/// allocation-free in steady state.
/// </para>
/// </remarks>
public sealed class AdvisorDispatch
{
    /// <summary>
    /// How many raises one frame can buffer.  The original has no queue at all — each site calls the
    /// dispatcher inline — but the port's sim step runs ahead of its frame, so the raises of one
    /// step have to wait.  Sixteen is far above anything measured (a busy pursuit frame raises one).
    /// </summary>
    public const int PendingCapacity = 16;

    private readonly AdvisorMessages _messages;
    private readonly Func<int>? _rand8;

    /// <summary><c>g_advisor_per_event_stamp [0xBCC6]</c> — <c>u16[21]</c>.</summary>
    private readonly ushort[] _stamp = new ushort[AdvisorTiming.CodeCount];

    private readonly byte[] _pendingCode = new byte[PendingCapacity];
    private readonly AdvisorDispatchKind[] _pendingKind = new AdvisorDispatchKind[PendingCapacity];
    private int _pendingCount;

    /// <summary><c>g_advisor_active_bitmask_lo/hi [0xBCFA]/[0xBCFC]</c>, as one 32-bit word.</summary>
    private uint _mask;

    private ushort _lastDispatch;
    private ushort _nextAllow;
    private string _line1 = string.Empty;
    private string _line2 = string.Empty;
    private AdvisorDisplayType _type = AdvisorDisplayType.Standard;

    /// <summary>Builds the dispatcher over a resolved message table.</summary>
    /// <param name="messages">The twenty-one message pairs, read from the data tree.</param>
    /// <param name="rand8">
    /// The kill advisory's gate: a draw in 0..255 from the <c>Fx.Indicators</c> stream.  Null makes
    /// code 0 always speak, which is what a deterministic test wants.
    /// </param>
    public AdvisorDispatch(AdvisorMessages messages, Func<int>? rand8 = null)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _rand8 = rand8;
    }

    /// <summary>How many raises the kernels made this sortie, before any gate.</summary>
    public int Raised { get; private set; }

    /// <summary>How many advisories actually reached the window this sortie.</summary>
    public int Spoken { get; private set; }

    /// <summary>How many raises were refused — by the cooldown, a one-shot bit or the random gate.</summary>
    public int Suppressed { get; private set; }

    /// <summary>How many raises were dropped because the frame's buffer was already full.</summary>
    public int Overflowed { get; private set; }

    /// <summary>The most recent action code that spoke, or -1.</summary>
    public int LastCode { get; private set; } = -1;

    /// <summary>The one-shot bitmask, for a test and for the census line.</summary>
    public uint Mask => _mask;

    /// <summary>
    /// A raise from one of the kernels' seams.  Cheap and allocation-free: it only records the code.
    /// </summary>
    /// <param name="code">The action code the original would have put in <c>AL</c>.</param>
    /// <param name="kind">Which of the three doors the site calls through.</param>
    public void Raise(byte code, AdvisorDispatchKind kind)
    {
        if (_pendingCount >= PendingCapacity)
        {
            Overflowed++;
            return;
        }

        Raised++;
        _pendingCode[_pendingCount] = code;
        _pendingKind[_pendingCount] = kind;
        _pendingCount++;
    }

    /// <summary>
    /// Runs every raise this frame collected and returns what the window should draw.
    /// </summary>
    /// <param name="now">
    /// <c>g_frame_count_scaled [0xF0D0]</c> — <c>TickClock.FrameCountScaled</c>.
    /// </param>
    /// <param name="masterNow">
    /// <c>g_master_frame_counter [0xF0C8]</c> — <c>TickClock.MasterFrameCounter</c>, the
    /// rate-limiter's clock.
    /// </param>
    /// <param name="aircraftIndex">
    /// <c>g_active_aircraft_idx [0xC31A]</c>, for code 16's branch.
    /// </param>
    /// <param name="statusFlags">
    /// <c>g_input_state_bitfield [0xF0BC]</c>, for the same.
    /// </param>
    /// <returns>The advisor window's state for this frame.</returns>
    public AdvisorPanelState Pump(ushort now, ushort masterNow, int aircraftIndex, int statusFlags)
    {
        for (int i = 0; i < _pendingCount; i++)
        {
            Run(_pendingCode[i], _pendingKind[i], now, masterNow, aircraftIndex, statusFlags);
        }

        _pendingCount = 0;
        return new AdvisorPanelState(Speaking(now), _line1, _line2, _type);
    }

    /// <summary>
    /// Whether a message's display window is open — the drawer's entry guard,
    /// <c>g_advisor_last_dispatch_time &lt;= now &lt; g_advisor_next_allow_time</c>
    /// (<c>image@0x0EEF0..0x0EEFD</c>; both compares are UNSIGNED <c>ja</c>).
    /// </summary>
    /// <param name="now"><c>g_frame_count_scaled [0xF0D0]</c>.</param>
    /// <returns>True while Chuck is speaking.</returns>
    public bool Speaking(ushort now) => _lastDispatch <= now && now < _nextAllow;

    /// <summary>
    /// <c>cockpit_advisor_state_reset @image@0x0EA41</c> — the teardown
    /// <c>cockpit_assets_load_all</c> runs when a sortie starts: the per-event stamps, the bitmask
    /// and the two timestamps all go to zero.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_stamp);
        _mask = 0;
        _lastDispatch = 0;
        _nextAllow = 0;
        _pendingCount = 0;
        _line1 = string.Empty;
        _line2 = string.Empty;
        _type = AdvisorDisplayType.Standard;
        LastCode = -1;
        Raised = 0;
        Spoken = 0;
        Suppressed = 0;
        Overflowed = 0;
    }

    private void Run(
        byte code, AdvisorDispatchKind kind, ushort now, ushort masterNow,
        int aircraftIndex, int statusFlags)
    {
        uint bit = code < 32 ? 1u << code : 0u;

        switch (kind)
        {
            case AdvisorDispatchKind.OneShot:
                // image@0x0F343..0x0F34D — the bit must still be clear.
                if ((_mask & bit) != 0)
                {
                    Suppressed++;
                    return;
                }

                break;

            case AdvisorDispatchKind.RateLimited:
                // image@0x0F36B..0x0F389 — first fire free; a repeat waits 0x3C master units.
                // (The stamp table has one entry per table code; the seven rate-limited sites all
                //  pass 6..19, so an out-of-table code — which the original would index past the
                //  array — cannot arise and is simply not throttled here.)
                if ((_mask & bit) != 0 && code < AdvisorTiming.CodeCount
                    && (ushort)(_stamp[code] + AdvisorTiming.RepeatThrottleUnits) >= masterNow)
                {
                    Suppressed++;
                    return;
                }

                if (Dispatch(code, now, aircraftIndex, statusFlags) && code < AdvisorTiming.CodeCount)
                {
                    // image@0x0F397..0x0F3A1 — the stamp is only written on a SUCCESSFUL dispatch.
                    _stamp[code] = masterNow;
                }

                return;

            default:
                break;
        }

        Dispatch(code, now, aircraftIndex, statusFlags);
    }

    /// <summary><c>ai_advisor_message_dispatch @image@0x0EFF1</c>, in the order it does things.</summary>
    private bool Dispatch(byte code, ushort now, int aircraftIndex, int statusFlags)
    {
        // image@0x0F00E — cmp [0xBCF2],ax / jb: the cooldown passes only when next_allow < now.
        // image@0x0F014 — unless the code is 2, the ejection advisory, which always speaks.
        if (_nextAllow >= now && code != AdvisorTiming.EjectionCode)
        {
            Suppressed++;
            return false;
        }

        // image@0x0F01A — last_dispatch = now + 2, BEFORE the handler runs.
        _lastDispatch = (ushort)(now + AdvisorTiming.DispatchDelay);

        // image@0x0F02B — and the one-shot bit is set BEFORE the handler too, so a kill advisory the
        // random gate suppresses still counts as "seen" for the two wrappers.
        if (code < 32)
        {
            _mask |= 1u << code;
        }

        // image@0x0F2D5 — a code above 20 skips the whole table and keeps display type 0.
        if (code < AdvisorTiming.CodeCount)
        {
            // image@0x0F03F..0x0F047 — the kill advisory's 50 % gate, and it returns without
            // touching the tail, so next_allow is left stale and nothing is drawn.
            if (code == AdvisorTiming.KillCode
                && _rand8 is { } draw && draw() > AdvisorTiming.KillGateThreshold)
            {
                Suppressed++;
                return false;
            }

            (_line1, _line2) = _messages.Lines(code, aircraftIndex, statusFlags);
            _type = AdvisorTiming.DisplayType(code);

            // image@0x0F176 (and image@0x0F0D1 for code 4) — the eight codes that appear at once.
            if (AdvisorTiming.RefreshesTimestamp(code))
            {
                _lastDispatch = now;
            }
        }
        else
        {
            _type = AdvisorDisplayType.Standard;
        }

        // image@0x0F32A — next_allow = last_dispatch + 0x10.
        _nextAllow = (ushort)(_lastDispatch + AdvisorTiming.DisplayUnits);
        Spoken++;
        LastCode = code;
        return true;
    }
}

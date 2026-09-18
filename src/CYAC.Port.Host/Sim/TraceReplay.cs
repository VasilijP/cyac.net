using System.Buffers.Binary;
using System.Globalization;
using CYAC.Port.Core.Data;
using CYAC.Port.Core.Model.Flight;
using CYAC.Port.Core.Primitives;
using CYAC.Port.Core.Sim.Flight;
using CYAC.Port.Core.Sim.Flight.ColdStart;
using CYAC.Port.Core.Sim.Flight.Trace;

namespace CYAC.Port.Host.Sim;

/// <summary>What one <see cref="TraceReplay"/> pass measured.</summary>
/// <param name="Frames">Complete flight frames compared.</param>
/// <param name="Exact">Frames whose whole post-step master block matched the trace's S7 record.</param>
/// <param name="Segments">How many times the loop was seeded (1 + every re-arm / gap resync).</param>
/// <param name="SegmentsExact">Segments that ran end to end with no divergent frame.</param>
/// <param name="Gaps">In-range record gaps — steps in which the flight chain did not run.</param>
/// <param name="ChannelWrites">Cross-frame writes the three NAMED external channels carried.</param>
/// <param name="UnmappedWrites">Cross-frame writes outside those channels (each forces a re-seed).</param>
/// <param name="FirstDivergence">The first mismatch, or null.</param>
/// <param name="FirstDivergenceStep">Its step, or null.</param>
/// <param name="RollAgreements">Frames whose roll moved the same way the stick was pushed.</param>
/// <param name="RollDisagreements">Frames whose roll moved the other way.</param>
/// <param name="PitchAgreements">Frames whose pitch moved the same way the Y axis was pushed.</param>
/// <param name="PitchDisagreements">Frames whose pitch moved the other way.</param>
/// <param name="ClimbAgreements">Frames climbing whose pitch BAM was positive, or descending with a negative one.</param>
/// <param name="ClimbDisagreements">Frames where the two disagreed.</param>
public readonly record struct TraceReplayResult(
    int Frames,
    int Exact,
    int Segments,
    int SegmentsExact,
    int Gaps,
    int ChannelWrites,
    int UnmappedWrites,
    string? FirstDivergence,
    uint? FirstDivergenceStep,
    int RollAgreements,
    int RollDisagreements,
    int PitchAgreements,
    int PitchDisagreements,
    int ClimbAgreements,
    int ClimbDisagreements);

/// <summary>
/// Replays a whole flight trace through the port's kernel from the trace's RECORDED inputs, and
/// checks every frame's post-step <c>s_aircraft_master</c> against the genuine machine's S7 record.
/// </summary>
/// <remarks>
/// <para>
/// This is the HOST-LOOP check, not a re-verification: the kernel's own verification is
/// <c>FlightKernelClosedLoopTests</c> (469,903 of 469,903 frames exact over 19 recordings), which
/// also RECOMPUTES every external-channel write from <c>CockpitKeys</c> and
/// <c>AircraftDamage</c>.  Here the three named channels are simply COPIED out of the trace, because
/// what is under test is the loop that seeds, steps and re-seeds — a divergence means the host, not
/// the kernel.  The channels are the same three K8/K9/an earlier pass named:
/// </para>
/// <list type="bullet">
///   <item>cockpit keys — <c>master[+0xA0..+0xA3]</c> and <c>[+0x124]</c>
///     (<c>cockpit_key_dispatch @image@0x2A3FF</c>);</item>
///   <item>flight end — <c>master[+0x122]</c>
///     (<c>scene_setup_or_camera_reset @image@0x2265A</c>);</item>
///   <item>combat damage — the eight fields <c>weapon_fire_combat_loop @image@0x0F748</c> and
///     <c>engagement_per_frame_tick @image@0x0FC51</c> write.</item>
/// </list>
/// <para>
/// <c>dt</c> comes from the record (<see cref="DtPolicy.Recorded"/>): the port's own clock is a
/// declared FIX, so a recording can only be reproduced under the recorded dt.
/// </para>
/// </remarks>
public sealed class TraceReplay
{
    /// <summary>
    /// The master offsets the three named external channels are allowed to move between frames.
    /// </summary>
    private static readonly (int Offset, int Length)[] NamedChannels =
    [
        (0x38, 8),      // combat: roll-DEAD control block ("AILERONS DAMAGED")
        (0xA0, 4),      // cockpit: throttle target (and damage type 0x01's re-clamp)
        (0xA4, 2),      // combat: hit points
        (0xC0, 4),      // combat: fuel drained by a leaking tank
        (0xDE, 4),      // combat: pitch control bounds ("ELEVATORS DAMAGED")
        (0xEC, 2),      // combat: induced-drag coefficient ("WING DAMAGED")
        (0xEE, 2),      // combat: elevator authority
        (0xF0, 2),      // combat: roll gain
        (0x122, 1),     // flight end: active / ejection state
        (0x123, 1),     // combat: damage flags
        (0x124, 1),     // cockpit: status flags (gear / flaps / brakes / afterburner)
    ];

    private readonly string _path;
    private readonly DataTree _tree;
    private readonly FlightSeedResult? _coldStart;

    /// <summary>Creates a replay over one trace.</summary>
    /// <param name="path">The <c>cyac-flight-trace</c> file.</param>
    /// <param name="tree">The data tree the aircraft definitions come from.</param>
    /// <param name="coldStart">
    /// When given, the FIRST seed is this port-built state instead of the trace's own record, so a
    /// clean pass verifies <c>FlightColdStart</c> as well as the host loop.  Later seeds (a record
    /// gap, an unmapped external write) still come from the trace, because no cold start can
    /// describe a mid-sortie resync.
    /// </param>
    public TraceReplay(string path, DataTree tree, FlightSeedResult? coldStart = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _path = path;
        _tree = tree;
        _coldStart = coldStart;
    }

    /// <summary>Runs the whole trace.</summary>
    /// <param name="perFrame">Called for every compared frame with its step and whether it matched.</param>
    public TraceReplayResult Run(Action<uint, bool>? perFrame = null)
    {
        using FlightTraceReader reader = FlightTraceReader.Open(_path);

        FlightTraceRecord?[] frame = new FlightTraceRecord?[8];
        uint step = uint.MaxValue;
        int filled = 0;
        FlightTraceRecord? previousPost = null;
        uint previousStep = 0;

        FlightKernelState? state = null;
        byte[] shadow = [];
        HostWorld world = new HostWorld();
        TraceRandom random = new TraceRandom();
        Dictionary<int, AircraftDefinition> definitions = new Dictionary<int, AircraftDefinition>();

        int frames = 0, exact = 0, segments = 0, segmentsExact = 0, gaps = 0;
        int channelWrites = 0, unmapped = 0;
        bool segmentBroken = false;
        string? firstDivergence = null;
        uint? firstDivergenceStep = null;
        int rollAgree = 0, rollDisagree = 0, pitchAgree = 0, pitchDisagree = 0;
        int climbAgree = 0, climbDisagree = 0;

        void Seed(in FlightTraceRecord pre)
        {
            if (segments > 0 && !segmentBroken)
            {
                segmentsExact++;
            }

            segments++;
            segmentBroken = false;

            int index = pre.GlobalWord(FlightTraceSeed.ActiveAircraftGlobal);
            if (segments == 1 && _coldStart is FlightSeedResult cold)
            {
                state = cold.State;
                shadow = [.. _tree.Aircraft[AircraftDefinition.FlyableBasenames[index]]
                    .RawFlightModel.Span];
                AircraftMasterCodec.WriteMasterBytes(state.Aircraft, shadow);
                return;
            }

            if (!definitions.TryGetValue(index, out AircraftDefinition? definition))
            {
                definition = _tree.Aircraft[AircraftDefinition.FlyableBasenames[index]];
                definitions[index] = definition;
            }

            state = FlightTraceSeed.CreateState(
                definition,
                pre,
                FlightTraceSeed.ReadCalibration(pre),
                FlightTraceSeed.ReadPullUpTuning(pre)).State;
            shadow = [.. pre.Master];
        }

        foreach (FlightTraceRecord record in reader.Records())
        {
            if (record.IsProbe)
            {
                continue;
            }

            if (record.Step != step)
            {
                step = record.Step;
                Array.Clear(frame);
                filled = 0;
            }

            if (record.Id is < 0 or > 7 || frame[record.Id] is not null)
            {
                continue;
            }

            frame[record.Id] = record.Copy();
            if (++filled != 8)
            {
                continue;
            }

            FlightTraceRecord pre = frame[0]!.Value;
            FlightTraceRecord post = frame[7]!.Value;

            if (previousPost is null)
            {
                Seed(pre);
            }
            else if (record.Step != previousStep + 1)
            {
                // An in-range record gap: the chain stopped running and code the kernel does not
                // model owned the state.  No closed loop can predict the other side.
                gaps++;
                Seed(pre);
            }
            else
            {
                (int named, int other) = ClassifyDelta(previousPost.Value.Master, pre.Master);
                channelWrites += named;
                if (other > 0)
                {
                    unmapped += other;
                    Seed(pre);
                }
                else if (named > 0)
                {
                    ApplyNamedChannels(state!, pre.Master, shadow);
                }
            }

            // Attitude-sign census: what the GENUINE machine's own records say about which way a
            // stick push moves the attitude.  Reported, never used by the kernel.
            CensusAttitudeSigns(
                pre, post, ref rollAgree, ref rollDisagree, ref pitchAgree, ref pitchDisagree);
            CensusClimbSign(pre, post, ref climbAgree, ref climbDisagree);

            FlightFrameInputs inputs = FlightTraceSeed.FrameInputs(pre);
            state!.Window.Calibration = FlightTraceSeed.ReadCalibration(pre);
            random.Reseed(pre.GlobalWord("g_prng_state"));
            world.LandingZoneAnswer = post.Master[0x122] != 0;

            FlightKernel.Step(
                state,
                inputs,
                FlightKernel.ResolveDt(DtPolicy.Recorded, state, null, inputs.RecordedDt),
                random,
                world,
                VelocityDynamics.Instance);

            frames++;
            AircraftMasterCodec.WriteMasterBytes(state.Aircraft, shadow);
            string? failure = FirstMismatch(post, shadow);
            perFrame?.Invoke(post.Step, failure is null);
            if (failure is null)
            {
                exact++;
            }
            else
            {
                segmentBroken = true;
                firstDivergence ??= failure;
                firstDivergenceStep ??= post.Step;
            }

            previousPost = post;
            previousStep = record.Step;
        }

        if (segments > 0 && !segmentBroken)
        {
            segmentsExact++;
        }

        return new TraceReplayResult(
            frames, exact, segments, segmentsExact, gaps, channelWrites, unmapped,
            firstDivergence, firstDivergenceStep,
            rollAgree, rollDisagree, pitchAgree, pitchDisagree, climbAgree, climbDisagree);
    }

    private static void CensusAttitudeSigns(
        in FlightTraceRecord pre,
        in FlightTraceRecord post,
        ref int rollAgree,
        ref int rollDisagree,
        ref int pitchAgree,
        ref int pitchDisagree)
    {
        (short x, short y) = FlightTraceSeed.ReadAxes(pre);

        int rollDelta = Angle.Normalize(unchecked((short)(
            BinaryPrimitives.ReadUInt16LittleEndian(post.PlayerObject[0x16..])
            - BinaryPrimitives.ReadUInt16LittleEndian(pre.PlayerObject[0x16..]))));
        if (x != 0 && rollDelta != 0)
        {
            if (Math.Sign(x) == Math.Sign(rollDelta))
            {
                rollAgree++;
            }
            else
            {
                rollDisagree++;
            }
        }

        int pitchDelta = Angle.Normalize(unchecked((short)(
            BinaryPrimitives.ReadUInt16LittleEndian(post.PlayerObject[0x14..])
            - BinaryPrimitives.ReadUInt16LittleEndian(pre.PlayerObject[0x14..]))));
        if (y != 0 && pitchDelta != 0)
        {
            if (Math.Sign(y) == Math.Sign(pitchDelta))
            {
                pitchAgree++;
            }
            else
            {
                pitchDisagree++;
            }
        }
    }

    /// <summary>
    /// What a POSITIVE pitch BAM means, settled by the genuine machine's own trajectory — does the
    /// aircraft gain altitude while its <c>WorldObject +0x14</c> is positive?
    /// </summary>
    /// <remarks>
    /// Only frames whose altitude moved by more than a foot are counted, so the level-flight noise
    /// (where <c>pos_y</c> jitters in Q8 units) cannot vote.
    /// </remarks>
    private static void CensusClimbSign(
        in FlightTraceRecord pre, in FlightTraceRecord post, ref int agree, ref int disagree)
    {
        int before = BinaryPrimitives.ReadInt32LittleEndian(pre.PlayerObject[0x0A..]);
        int after = BinaryPrimitives.ReadInt32LittleEndian(post.PlayerObject[0x0A..]);
        int climb = after - before;
        if (Math.Abs(climb) <= 0x100)
        {
            return;
        }

        int pitch = Angle.Normalize(
            unchecked((short)BinaryPrimitives.ReadUInt16LittleEndian(post.PlayerObject[0x14..])));
        if (pitch == 0)
        {
            return;
        }

        if (Math.Sign(pitch) == Math.Sign(climb))
        {
            agree++;
        }
        else
        {
            disagree++;
        }
    }

    private static (int Named, int Other) ClassifyDelta(
        ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        int named = 0, other = 0;
        for (int offset = 0; offset < before.Length; offset++)
        {
            if (before[offset] == after[offset])
            {
                continue;
            }

            if (IsNamedChannel(offset))
            {
                named++;
            }
            else
            {
                other++;
            }
        }

        return (named, other);
    }

    private static bool IsNamedChannel(int offset)
    {
        foreach ((int start, int length) in NamedChannels)
        {
            if (offset >= start && offset < start + length)
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyNamedChannels(
        FlightKernelState state, ReadOnlySpan<byte> genuine, byte[] shadow)
    {
        // Write the genuine bytes over the shadow, then push the whole block back into the port's
        // aircraft through the ported codec — the same round trip the comparison uses.
        foreach ((int start, int length) in NamedChannels)
        {
            genuine.Slice(start, length).CopyTo(shadow.AsSpan(start, length));
        }

        AircraftMasterCodec.ApplyMasterBytes(shadow, state.Aircraft);
    }

    private static string? FirstMismatch(in FlightTraceRecord post, byte[] shadow)
    {
        ReadOnlySpan<byte> genuine = post.Master;
        for (int i = 0; i < genuine.Length; i++)
        {
            // +0x11A..+0x121 are the two real-mode seg:off far pointers (the player world object and
            // the loaded .fme).  A trace seed copies them in; a cold start cannot invent guest
            // addresses, and nothing in the kernel reads them as numbers (Aircraft's remarks).
            if (i is >= 0x11A and <= 0x121)
            {
                continue;
            }

            if (genuine[i] != shadow[i])
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"step {post.Step} S7 master +0x{i:X3}: genuine 0x{genuine[i]:X2}, port 0x{shadow[i]:X2}");
            }
        }

        return null;
    }

    /// <summary>The kernel's one draw, from the frame's recorded <c>g_prng_state [0x07A8]</c>.</summary>
    private sealed class TraceRandom : IKernelRandom
    {
        private Lfsr16 _lfsr;

        public void Reseed(ushort state) => _lfsr = Lfsr16.FromRawState(state);

        public byte NextRand8() => unchecked((byte)_lfsr.Step(8));
    }
}

namespace CYAC.Port.Audio;

/// <summary>
/// The port's SFX tone-generator engine: <c>adldrive.drv</c>'s 34 procedural tone generators,
/// driven by the driver-call stream and rendered at 48 kHz in floating point.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance.</b> This is the REFINED-SFX model of the original's own sound driver — the same
/// generator engine, decoded from the driver module's bytes and transcribed into the port.  Every
/// address literal below is an offset into that module, so it can be checked against it.
/// </para>
/// <para>
/// <b>"REPRESENT, don't reproduce", applied to audio.</b>  The SAME 1991 sound design — the same
/// tone programs, envelope and frequency ramps, the same noise-table per-shot variation — rendered
/// at 48 kHz with per-sample interpolation between the driver's 256 Hz ticks, with no 6-bit TL
/// quantization and no 10-bit F-Number quantization.
/// </para>
/// <para>
/// <b>State lives where the driver keeps it.</b>  The model operates directly on a mutable copy of
/// the module's own image (<see cref="AdlDriverImage"/>), so an index into that array IS the
/// driver's own DS offset and every address literal is lifted verbatim from the disassembly.  A
/// pleasant consequence: the bespoke <c>cmd 0x04</c> update stubs, which poke ABSOLUTE state-block
/// addresses, are one-line <see cref="Write"/> calls.
/// </para>
/// <para>
/// <b>Field-map source of truth:</b> (header <c>+0x00..+0x1C</c> plus <c>+0x36</c>; The routines
/// mirrored here are, in driver space: <c>cmd00 @0x00AB</c>, <c>cmd02 @0x014B</c>, <c>cmd06
/// @0x02D5</c>, <c>cmd08 @0x01F7</c>, <c>cmd0A @0x022B</c>, per-tone walk <c>sub_0369</c>, seed
/// <c>sub_063D</c>, per-tick <c>sub_06C8</c>, key <c>sub_0578</c>, TL <c>sub_0535</c>, patch program
/// <c>sub_0445</c>, key-off sweep <c>sub_019F</c>, and the cmd04 update stubs
/// <c>@0x1851/0x1860/0x1873/0x1886/0x1911/0x1921/0x1931</c> (<c>asset:1b/adldrive.drv@FILEOFF</c> =
/// these + 0x10).
/// </para>
/// <para>
/// <b>Not modelled</b> (deliberately): the <c>.SNG</c> sequencer, the music patch bank and channel
/// volume CC — music gets its own voice on the mixer when it is built.  The one music fact this
/// model needs is the driver's own mutual exclusion: <c>cmd06</c> returns before the SFX engine
/// whenever a song is loaded (drv <c>@0x02D9..0x0321</c>), so a loaded song FREEZES the generators.
/// </para>
/// <para>
/// <b>Deterministic:</b> no RNG, no clock, no host state.  A render is a pure function of
/// (driver image, call stream, sample cadence) — <c>AdlToneEngineDeterminismTests</c> asserts it.
/// </para>
/// </remarks>
public sealed class AdlToneEngine
{
    // ------------------------------------------------------------------ driver-space constants
    // Every value is an ADDRESS LITERAL from the adl disassembly, i.e. exactly what the driver's
    // own code uses with DS = its load segment.
    private const int DescBase = AdlDriverImage.DescriptorTable;   // `add si,0x1568` @drv 0x00C7
    private const int DescCount = AdlDriverImage.DescriptorCount;  // `cmp ax,0x22` @drv 0x0160
    private const int DescStride = AdlDriverImage.DescriptorStride;
    private const int SlotArray = 0xB05;     // 4 SFX slots (word each), indexed by chan_slot 0/2/4/6
    private const int ToneIdArray = 0xB0D;   // parallel owner-tone-id array
    private const int Timebase = 0xB15;      // += 3 per tick @drv 0x0322; noise cursor (& 0x1FF)
    private const int ChanBitTbl = 0xB42;    // 11 words: 1 << channel
    private const int OpOffTbl = 0xB87;      // 2 bytes/channel: {modulator, carrier} operator offset
    private const int NoiseTbl = 0xBA9;      // `imul word ptr [di+0xba9]` — 512-byte signed noise table
    private const int SegStride = 0x36;      // one segment per set slot-mask bit
    private const int MaxChannels = 11;      // 9 melodic + 5 rhythm slots share 11 mask bits

    // header fields (offsets from the state block's base)
    private const int HSlotMask = 0x00, HMaskWork = 0x02, HStolen = 0x04, HLoopCount = 0x06;
    private const int HDur1Cur = 0x08, HDur2Cur = 0x0A, HNextA = 0x0C, HNextB = 0x0E;
    private const int HRetrigA = 0x10, HRetrigB = 0x12, HDur1Base = 0x14, HDur1Rand = 0x16;
    private const int HDur2Base = 0x18, HDur2Rand = 0x1A, HLoopInit = 0x1C, HRepatch = 0x36;

    // segment fields (offsets from the SEGMENT base = block + 0x36*index)
    private const int SFnum = 0x1E, SFreqAcc = 0x20, SOctCur = 0x22, SFreqSlope = 0x24;
    private const int STlCur = 0x26, SAmpAcc = 0x28, SDurCur = 0x2A, SLfoPhase = 0x2C;
    private const int SLfoDepth = 0x2E, SFreqBase = 0x30, SFreqRand = 0x32, SOctave = 0x34;
    private const int SSlopeBase = 0x38, SSlopeRand = 0x3A, SAmpBase = 0x3C, SAmpSlope = 0x3E;
    private const int SDuration = 0x40, SWavetable = 0x42, SLfoPeriod = 0x44, SLfoRate = 0x46;
    private const int SPatch = 0x48;         // 12 bytes: 5 mod regs, 5 car regs, 0xC0 byte, BD byte

    private readonly byte[] _d;
    private readonly FmVoice[] _voices = new FmVoice[MaxChannels];

    private bool _musicLoaded;      // drv [0xEC9] != 0 — the tick then never reaches the SFX engine
    private bool _paused;           // drv [0xB19] (cmd 0x0A / 0x0C)
    private int _activeMask;        // drv [0xB40] — per-tick channel arbitration
    private long _lastTickSample = -1;
    private double _rampSamples = AudioFormat.SamplesPerTick;

    /// <summary>Builds an engine over a private copy of the driver's data segment.</summary>
    /// <param name="image">The validated module.</param>
    public AdlToneEngine(AdlDriverImage image)
        : this((image ?? throw new ArgumentNullException(nameof(image))).ToMutableCopy())
    {
    }

    /// <summary>Builds an engine directly over driver-space bytes it may mutate.</summary>
    /// <param name="driverSpaceBytes">
    /// The module's bytes, index 0 == <c>drv:0</c>.  Taken by reference and MUTATED, exactly as the
    /// guest driver mutates its own data segment — pass a copy you own.
    /// </param>
    public AdlToneEngine(byte[] driverSpaceBytes)
    {
        ArgumentNullException.ThrowIfNull(driverSpaceBytes);
        _d = driverSpaceBytes;
        for (int i = 0; i < _voices.Length; i++)
        {
            _voices[i] = new FmVoice();
        }
    }

    /// <summary>The OPL channel bits (0..8) this engine is currently voicing.</summary>
    /// <remarks>
    /// Rhythm mask slots 9 (CYM) and 10 (HH) live on OPL channels 8 and 7.  The port has no second
    /// OPL renderer to arbitrate against, so this is diagnostics — and the hook a music voice would
    /// need if one is ever built on the same chip model.
    /// </remarks>
    public int OwnedChannelMask { get; private set; }

    /// <summary>True once the engine has seen its first tick.</summary>
    public bool Ticked { get; private set; }

    /// <summary>How many <c>cmd00</c> starts the engine instantiated.</summary>
    public int Starts { get; private set; }

    /// <summary>How many key-on / retrigger events it produced.</summary>
    public int KeyOns { get; private set; }

    /// <summary>Whether a <c>.SNG</c> is loaded, which freezes the SFX generators.</summary>
    public bool MusicLoaded => _musicLoaded;

    // ---------------------------------------------------------------- driver-image accessors
    private ushort Read(int a) =>
        (uint)(a + 1) < (uint)_d.Length ? (ushort)(_d[a] | (_d[a + 1] << 8)) : (ushort)0;

    private short ReadSigned(int a) => (short)Read(a);

    private void Write(int a, int v)
    {
        if ((uint)(a + 1) >= (uint)_d.Length)
        {
            return;
        }

        _d[a] = (byte)v;
        _d[a + 1] = (byte)(v >> 8);
    }

    /// <summary>
    /// <c>imul word ptr [di+0xba9]</c> — the driver's 512-byte signed noise table, the ONLY source
    /// of per-shot variation, because all 206 descriptors carry NULL
    /// pitch/volume override pointers.
    /// </summary>
    /// <remarks>
    /// The cursor <c>[0xB15]</c> is masked to <c>0x1FF</c> at the top of every per-tick call
    /// (drv <c>@0x06D2</c>), so it normally stays in range; the fallback folds an out-of-range
    /// cursor back into the table instead of reading past the module.
    /// </remarks>
    private short Noise(int di)
    {
        int a = NoiseTbl + di;
        if ((uint)(a + 1) >= (uint)_d.Length)
        {
            a = NoiseTbl + (di & 0x1FE);
        }

        return ReadSigned(a);
    }

    /// <summary>
    /// The driver's randomized-quantity idiom: <c>value = base + hi16(rand × noise)</c> — the
    /// <c>imul</c> is SIGNED and <c>dx</c> is the high word.
    /// </summary>
    private static int RandomizedSeed(short rand, short noise, ushort baseValue)
        => (ushort)(((rand * noise) >> 16) + baseValue);

    // ---------------------------------------------------------------- command entry

    /// <summary>Consumes one driver call.</summary>
    /// <param name="command">The driver command word (even, 0x00..0x1E).</param>
    /// <param name="arg0">First positional argument — the tone id for the SFX commands.</param>
    /// <param name="arg1">Second — the pitch for a continuous tone.</param>
    /// <param name="arg2">Third — the volume for a continuous tone.</param>
    /// <param name="sample">
    /// The 48 kHz output sample this call lands at; it anchors the interpolation of every ramp the
    /// call moves.
    /// </param>
    public void ApplyCommand(ushort command, ushort arg0, ushort arg1, ushort arg2, long sample)
    {
        switch (command)
        {
            case 0x00: StartTone(arg0, arg1, arg2, sample); break;
            case 0x02: StopTone(arg0); break;
            case 0x04: UpdateContinuous(arg0, arg1, arg2); break;
            case 0x06: Tick(sample); break;
            case 0x08:
            case 0x18: Silence(); break;                 // cmd 0x18 IS cmd 8's routine
            case 0x0A: _paused = true; SilenceAll(); break;
            case 0x0C: _paused = false; break;
            case 0x16: _musicLoaded = true; SilenceAll(); break;   // cmd16 also re-inits the chip
            case 0x12: break;                                      // init — nothing SFX-visible
            default: break;                                        // the dormant driver-only cmds
        }
    }

    // -------------------------------------------------------- cmd 0x00 — start tone (drv @0x00AB)
    private void StartTone(ushort toneId, ushort pitch, ushort volume, long sample)
    {
        if (toneId >= DescCount)
        {
            return;                                      // `cmp ax,0x22` — the closure bound
        }

        int desc = DescBase + (DescStride * toneId);
        int block = Read(desc);
        int slot = Read(desc + 2);
        if (block == 0)
        {
            return;
        }

        // Pre-empt: if the slot already holds a block, key OFF exactly the channels the OUTGOING
        // block owns that the incoming one does not (`dx = old & (old ^ new)` drv @0x00E3..0x00EB).
        int previous = Read(SlotArray + slot);
        if (previous != 0)
        {
            int oldMask = Read(previous);
            int newMask = Read(block);
            int drop = oldMask & (oldMask ^ newMask);
            if (drop != 0)
            {
                KeyOffMask(drop);
            }
        }

        // Generic start tail (tbl1678_05 drv @0x0103 + loc_012A): claim the slot, stamp the owner
        // tone id, clear the working and stolen masks, reload the loop counter.
        Write(SlotArray + slot, block);
        Write(ToneIdArray + slot, StampedToneId(toneId));

        // The specialised start stubs (drv 0x1678 table) run the SAME body as their cmd04 entry and
        // read the engine's pitch/volume off the very same stack frame, so the CONTINUOUS tones do
        // consume pitch/volume at start.  Only the DESCRIPTOR-level override pointers are null,
        // which is what the driver does.
        UpdateContinuous(toneId, pitch, volume, startStub: true);
        Write(block + HMaskWork, 0);
        Write(block + HStolen, 0);
        Write(block + HLoopCount, Read(block + HLoopInit));
        Starts++;
        _lastTickSample = sample;
    }

    /// <summary>
    /// What <c>tbl1678_05</c> (<c>asset:1b/adldrive.drv@0x0113</c>) actually stores at
    /// <c>[chan_slot + 0xB0D]</c>: CX as the bespoke start stub left it.
    /// </summary>
    /// <remarks>
    /// A SHIPPED DRIVER BUG.  Six of the eight bespoke start stubs clobber CX
    /// before the generic tail stores it: tones 0x02-0x04 and 0x0B stamp 6 (a leftover shift count)
    /// and tones 0x00/0x01 stamp the frequency word at drv 0x1535.  Mirrored so this engine's image
    /// matches the guest's byte for byte; the field is only read on the chain-end auto-stop path,
    /// which this model reaches by its own bookkeeping, so it is inert for the render.
    /// </remarks>
    private ushort StampedToneId(ushort toneId) => toneId switch
    {
        0x00 or 0x01 => Read(0x1535),
        0x02 or 0x03 or 0x04 or 0x0B => 6,
        _ => toneId,
    };

    // --------------------------------------------------------- cmd 0x02 — stop tone (drv @0x014B)
    private void StopTone(ushort toneId)
    {
        if (toneId >= DescCount)
        {
            return;
        }

        int desc = DescBase + (DescStride * toneId);
        int slot = Read(desc + 2);
        int block = Read(SlotArray + slot);
        if (block == 0)
        {
            return;
        }

        KeyOffMask(Read(block + HSlotMask));
        Write(SlotArray + slot, 0);                      // [0xB3C]==1 on the cmd02 path drv @0x018E
        RecomputeOwned();
    }

    // ------------------------------------------------------ cmd 0x08 / 0x18 — silence (drv @0x01F7)
    private void Silence()
    {
        _musicLoaded = false;
        for (int s = 0; s <= 6; s += 2)
        {
            Write(SlotArray + s, 0);
        }

        SilenceAll();
    }

    private void SilenceAll()
    {
        for (int c = 0; c < MaxChannels; c++)
        {
            _voices[c].KeyOff();
        }

        _activeMask = 0;
        OwnedChannelMask = 0;
    }

    private void KeyOffMask(int mask)
    {
        for (int c = 0; c < MaxChannels; c++)
        {
            if ((mask >> c & 1) != 0)
            {
                _voices[c].KeyOff();
            }
        }

        RecomputeOwned();
    }

    // -------------------------------------------------- cmd 0x04 — continuous-tone update

    /// <summary>
    /// The seven live entries of the cmd04 update table (drv 0x16BC): tones 0x00-0x04, 0x0B and
    /// 0x11.
    /// </summary>
    /// <remarks>
    /// Each one folds the engine's (pitch, volume) pair into ABSOLUTE state-block words, which is
    /// why they transcribe to plain <see cref="Write"/> calls.  The specialised START stubs (drv
    /// 0x1678 entries for the same tones, plus 0x0C, 0x15 and 0x16) call the identical bodies before
    /// falling through to the generic start, so <paramref name="startStub"/> re-uses them.
    /// </remarks>
    private void UpdateContinuous(ushort toneId, ushort pitch, ushort volume, bool startStub = false)
    {
        switch (toneId)
        {
            case 0x00: UpdateThreeSegments(pitch, volume, 0x1967); break;          // drv @0x1860
            case 0x01: UpdateThreeSegments(pitch, volume, 0x1A27); break;          // drv @0x1873
            case 0x02:                                                             // drv @0x1886
            {
                UpdateThreeSegments(pitch, volume, 0x1AE7);
                int amp = volume >= 5 ? volume - 5 : volume;
                amp <<= 6;
                Write(0x1BA7 + SAmpBase, amp);
                Write(0x1BA7 + SegStride + SAmpBase, amp);
                Write(0x1BA7 + (2 * SegStride) + SAmpBase, amp);
                break;
            }

            case 0x03:                                                             // drv @0x1911
                Write(0x1C97, (pitch + 2) << 6);
                Write(0x1CA3, volume << 6);
                break;
            case 0x04:                                                             // drv @0x1921
                Write(0x1CEB, pitch << 6);
                Write(0x1CF7, volume << 6);
                break;
            case 0x0B:                                                             // drv @0x1851
                Write(0x21F5, pitch << 6);
                break;
            case 0x11:                                                             // drv @0x1931
                Write(0x2485, 0x6C - pitch);
                Write(0x2489, 0x6C - pitch);
                Write(0x24A1, ((7 * volume) + 0x113) << 6);
                break;
            case 0x0C when startStub:                                              // drv @0x1838
                Write(0x2229, pitch);
                Write(0x2221, 0x65 - volume);
                break;
            case 0x15 when startStub:                                              // drv @0x1771
                UpdateArc(pitch, volume, 0x26B3, 0x26DB, 0x2711, 0x26DD, 0x2713);
                break;
            case 0x16 when startStub:                                              // drv @0x17B8
                UpdateArc(pitch, volume, 0x273D, 0x2765, 0x279B, 0x2767, 0x279D);
                break;
            default: break;
        }
    }

    /// <summary>
    /// <c>sub_179F</c> / <c>sub_17E6</c> — the bespoke start stubs of tones 0x15 and 0x16, which
    /// share three helpers: <c>sub_17FF</c> clamps the pitch into 1..6 (writing the clamp back to
    /// drv <c>[0x1523]</c>) and scales it by 100, <c>sub_181D</c> is <c>vol &lt;&lt; 6</c>, and
    /// <c>sub_1826</c> is <c>−(vol / 10)</c>.
    /// </summary>
    private void UpdateArc(
        ushort pitch, ushort volume, int aTarget, int bTarget1, int bTarget2, int cTarget1, int cTarget2)
    {
        int p = pitch == 0 ? 1 : pitch > 6 ? 6 : pitch;
        Write(0x1523, p);
        Write(0x1525, volume);
        Write(aTarget, p * 0x64);
        int b = volume << 6;
        Write(bTarget1, b);
        Write(bTarget2, b);
        int c = -(volume / 10);
        Write(cTarget1, c);
        Write(cTarget2, c);
    }

    /// <summary>
    /// <c>sub_18BB</c> — the shared three-segment update used by tones 0x00-0x02: segment 0 takes
    /// <c>amp_base = vol &lt;&lt; 6</c> and <c>freq_base = ((pitch × 15) / 10 + 0xE8) &lt;&lt; 6</c>;
    /// segments 1 and 2 take the same amp and <c>lfo_depth = pitch</c>.
    /// </summary>
    private void UpdateThreeSegments(ushort pitch, ushort volume, int segment0)
    {
        int amp = volume << 6;
        int freq = ((pitch * 15 / 10) + 0xE8) << 6;
        Write(segment0 + SAmpBase, amp);
        Write(segment0 + SFreqBase, freq);
        Write(segment0 + SegStride + SAmpBase, amp);
        Write(segment0 + SegStride + SLfoDepth, pitch);
        Write(segment0 + (2 * SegStride) + SAmpBase, amp);
        Write(segment0 + (2 * SegStride) + SLfoDepth, pitch);
    }

    // -------------------------------------------------------------- cmd 0x06 — tick (drv @0x02D5)
    private void Tick(long sample)
    {
        // The music sequencer owns the tick while a song is loaded and RETURNS before the SFX
        // engine (drv @0x02D9 `cmp [0xEC9],0` / @0x0321 `ret`) — the generators genuinely freeze
        // during music.
        if (_musicLoaded)
        {
            return;
        }

        if (_lastTickSample >= 0 && sample > _lastTickSample)
        {
            double dt = sample - _lastTickSample;
            if (dt > 4 && dt < 4 * AudioFormat.SamplesPerTick * 4)
            {
                _rampSamples = dt;
            }
        }

        _lastTickSample = sample;
        Ticked = true;

        Write(Timebase, Read(Timebase) + 3);
        _activeMask = _paused ? 0xFFFF : 0;
        for (int slot = 6; slot >= 0; slot -= 2)
        {
            if (Read(SlotArray + slot) != 0)
            {
                WalkSlot(slot, sample);
            }
        }

        RecomputeOwned();
    }

    /// <summary>
    /// <c>sub_0369</c> — walk one SFX slot's channels.  Segments are numbered by the running count
    /// of set bits in the block's slot mask.  A channel not yet in the WORKING mask is
    /// SEEDED (and its patch programmed) this tick; one already in it is TICKED.
    /// </summary>
    private void WalkSlot(int slot, long sample)
    {
        int entryBlock = Read(SlotArray + slot);
        int block = entryBlock;
        int segmentIndex = -1;
        for (int channel = 0; channel < MaxChannels; channel++)
        {
            int bit = Read(ChanBitTbl + (2 * channel));
            if (bit == 0 || (Read(block + HSlotMask) & bit) == 0)
            {
                continue;
            }

            segmentIndex++;
            bool busy = false, restolen = false;              // drv [0xB61] / [0xB63]
            if ((_activeMask & bit) != 0)
            {
                busy = true;                                  // another tone already holds it
                Write(block + HStolen, Read(block + HStolen) | bit);
            }
            else
            {
                _activeMask |= bit;
                if ((Read(block + HStolen) & bit) != 0)
                {
                    restolen = true;                          // got it back — reprogram the patch
                    Write(block + HStolen, Read(block + HStolen) & ~bit);
                }
            }

            if ((Read(block + HMaskWork) & bit) == 0)
            {
                Write(block + HMaskWork, Read(block + HMaskWork) | bit);
                Seed(block, segmentIndex);
                if (!busy)
                {
                    ProgramPatch(block, segmentIndex, channel, keyMode: 2, sample);
                }

                _activeMask |= bit;
            }
            else
            {
                if (restolen)
                {
                    ProgramPatch(block, segmentIndex, channel, keyMode: 2, sample);
                }

                TickSegment(ref block, slot, segmentIndex, channel, busy, sample);
                int now = Read(SlotArray + slot);
                if (now == 0 || now != entryBlock)
                {
                    return;                                   // a chain or stop changed the block
                }
            }
        }
    }

    /// <summary>
    /// <c>sub_063D</c> — seed one segment (and, at segment 0, the header's dur1/dur2 pair) from its
    /// base+rand pairs against the noise table.  Four randomized quantities per full seed, the
    /// cursor walking 2 bytes each.
    /// </summary>
    private void Seed(int block, int segmentIndex)
    {
        int segment = block + (SegStride * segmentIndex);
        int di = Read(Timebase);
        if (segmentIndex == 0)
        {
            Write(
                block + HDur1Cur,
                RandomizedSeed(ReadSigned(block + HDur1Rand), Noise(di), Read(block + HDur1Base)));
            di += 2;
            Write(
                block + HDur2Cur,
                RandomizedSeed(ReadSigned(block + HDur2Rand), Noise(di), Read(block + HDur2Base)));
            di += 2;
        }

        int accumulator = RandomizedSeed(
            ReadSigned(segment + SFreqRand), Noise(di), Read(segment + SFreqBase));
        di += 2;
        Write(segment + SFreqAcc, accumulator);
        Write(segment + SFnum, accumulator >> 6);
        Write(segment + SOctCur, Read(segment + SOctave));
        Write(
            segment + SFreqSlope,
            RandomizedSeed(ReadSigned(segment + SSlopeRand), Noise(di), Read(segment + SSlopeBase)));
        Write(segment + SDurCur, Read(segment + SDuration));
        int amp = Read(segment + SAmpBase);
        Write(segment + SAmpAcc, amp);
        Write(segment + STlCur, (amp ^ 0x0FFF) >> 6);
    }

    /// <summary>
    /// <c>sub_06C8</c> — one tick of one segment: frequency ramp plus wavetable LFO, TL ramp, the
    /// per-segment duration, and (at segment 0) the header's dur1 active / dur2 RELEASE countdown
    /// and the chain transition.  Where the driver writes OPL registers, this moves the host voice.
    /// </summary>
    private void TickSegment(ref int block, int slot, int segmentIndex, int channel, bool busy, long sample)
    {
        int di = Read(Timebase) & 0x1FF;
        Write(Timebase, di);                                  // drv @0x06D2 — the cursor stays bounded
        int segment = block + (SegStride * segmentIndex);

        if (segmentIndex == 0)
        {
            if (Read(block + HDur1Cur) == 0)
            {
                ChainOrRelease(ref block, slot, sample);
                return;
            }

            Write(block + HDur1Cur, Read(block + HDur1Cur) - 1);
            if (Read(block + HDur1Cur) == 0 && Read(block + HDur2Cur) != 0)
            {
                // dur1 just expired with a release tail pending.  The driver drops into the
                // transition path with dx=0 (drv @0x070C), which re-enters sub_0147 from the stored
                // tone id — whose only effect on that path is sub_019F: rewrite reg 0xB0+ch for
                // every channel in the mask WITHOUT the key bit = KEY OFF.  The slot is RETAINED
                // ([0xB3C]==0 at drv @0x018C), so dur2 then rings the release out tick by tick.
                KeyOffMask(Read(block + HSlotMask));
                return;
            }
        }

        int oldFnum = Read(segment + SFnum);
        Write(segment + SFreqAcc, Read(segment + SFreqAcc) + ReadSigned(segment + SFreqSlope));
        int wave = ReadSigned(Read(segment + SWavetable) + Read(segment + SLfoPhase));
        int lfo = (ReadSigned(segment + SLfoDepth) * wave) >> 16;
        int fnum = (ushort)(lfo + (Read(segment + SFreqAcc) >> 6));
        Write(segment + SFnum, fnum);

        // The LFO phase walks on the BLOCK's (segment 0's) fields — shipped behaviour, drv @0x073C.
        int phase = Read(block + SLfoPhase) + Read(block + SLfoRate);
        if (phase >= Read(block + SLfoPeriod))
        {
            phase -= Read(block + SLfoPeriod);
        }

        Write(block + SLfoPhase, phase);

        // Key phase (drv @0x075D): the driver only touches A0/B0 when the fnum CHANGED.  The key
        // mode is 1 while dur1 runs and 0 (KEY OFF = OPL release) once dur1 is spent but dur2 is
        // not: adl dur2 is the RELEASE phase.
        int keyMode = oldFnum != fnum
            ? (Read(block + HDur1Cur) != 0 || Read(block + HDur2Cur) == 0 ? 1 : 0)
            : -1;

        Write(segment + SAmpAcc, Read(segment + SAmpAcc) + ReadSigned(segment + SAmpSlope));
        Write(segment + STlCur, (Read(segment + SAmpAcc) ^ 0x0FFF) >> 6);

        if (!busy)
        {
            UpdateVoice(channel, segment, keyMode, sample);
        }

        if (Read(segment + SDurCur) != 0)
        {
            Write(segment + SDurCur, Read(segment + SDurCur) - 1);
            if (Read(segment + SDurCur) == 0)
            {
                _voices[channel].KeyOff();                    // drv loc_05FB — silence this segment
            }
        }
    }

    /// <summary>
    /// <c>loc_07C4</c>/<c>loc_07D0</c> — dur1 spent: run down dur2 (the release tail), then take the
    /// chain (<c>next_a</c>, or <c>next_b</c> once the loop counter expires), staging the paired
    /// RETRIGGER flag and the per-tone repatch flag.  <c>next == 0</c> ends the tone.
    /// </summary>
    private void ChainOrRelease(ref int block, int slot, long sample)
    {
        if (Read(block + HDur2Cur) != 0)
        {
            Write(block + HDur2Cur, Read(block + HDur2Cur) - 1);
            return;
        }

        int repatch = Read(block + HRepatch);                 // drv [0xB6F]
        int retrigger = Read(block + HRetrigA);               // drv [0xB38]
        int next = Read(block + HNextA);
        if (Read(block + HLoopCount) != 0)
        {
            Write(block + HLoopCount, Read(block + HLoopCount) - 1);
            if (Read(block + HLoopCount) == 0)
            {
                retrigger = Read(block + HRetrigB);
                next = Read(block + HNextB);
            }
        }

        if (next == 0)
        {
            ChainEnd(slot);
            return;
        }

        if (next != block)
        {
            Write(next + HLoopCount, Read(next + HLoopInit));
        }

        Write(SlotArray + slot, next);
        Write(next + HMaskWork, Read(next + HSlotMask));      // drv @0x086D working-mask copy

        // sub_0952: per set mask bit, re-seed (index 0 re-seeds the header durations too),
        // optionally reprogram the patch, and key per the staged retrigger flag
        // (1 = re-attack, else legato; rhythm always retriggers, drv @0x0A22).
        int segmentIndex = -1;
        for (int channel = 0; channel < MaxChannels; channel++)
        {
            if ((Read(next + HSlotMask) >> channel & 1) == 0)
            {
                continue;
            }

            segmentIndex++;
            Seed(next, segmentIndex);
            int mode = retrigger == 1 || channel >= 6 ? 2 : 1;
            if (repatch != 0)
            {
                ProgramPatch(next, segmentIndex, channel, mode, sample);
            }
            else
            {
                UpdateVoice(channel, next + (SegStride * segmentIndex), mode, sample);
            }
        }

        block = next;
    }

    private void ChainEnd(int slot)
    {
        int block = Read(SlotArray + slot);
        if (block != 0)
        {
            KeyOffMask(Read(block + HSlotMask));
        }

        Write(SlotArray + slot, 0);
        RecomputeOwned();
    }

    // ---------------------------------------------------------------- voice programming

    /// <summary>
    /// <c>sub_0445</c> — program the segment's EMBEDDED 12-byte FM patch record (segment
    /// <c>+0x48..+0x53</c> = 5 modulator register bytes, 5 carrier bytes, the reg-0xC0
    /// feedback/connection byte and the rhythm BD merge byte) and key the voice.
    /// </summary>
    /// <remarks>
    /// The live TL merge (<c>sub_0515</c>) replaces the patch byte's low 6 bits with
    /// <c>tl_cur</c> — for the carrier always, for the modulator only when the connection bit
    /// (<c>+0x52</c> bit 0) is set.
    /// </remarks>
    private void ProgramPatch(int block, int segmentIndex, int channel, int keyMode, long sample)
    {
        int segment = block + (SegStride * segmentIndex);
        FmVoice voice = _voices[channel];
        for (int i = 0; i < 12; i++)
        {
            voice.Patch[i] =
                (uint)(segment + SPatch + i) < (uint)_d.Length ? _d[segment + SPatch + i] : (byte)0;
        }

        int carrierOffset = OpOffTbl + (2 * channel) + 1;
        voice.SingleOperator =
            (uint)carrierOffset >= (uint)_d.Length || _d[carrierOffset] == 0xFF;   // rhythm slots
        voice.AdditiveConnection = (voice.Patch[10] & 1) != 0;
        voice.Feedback = (voice.Patch[10] >> 1) & 7;
        UpdateVoice(channel, segment, keyMode, sample);
    }

    /// <summary>
    /// Pushes one segment's live ramp state into its host voice: the un-quantized F-Number
    /// (<c>freq_accum / 64</c> plus the LFO's exact product instead of <c>&gt;&gt; 6</c> and
    /// <c>hi16</c>), the un-quantized attenuation (<c>(amp_accum ^ 0xFFF) / 64</c> instead of the
    /// 6-bit TL register) and the key transition.  Both targets are INTERPOLATED to over the next
    /// tick period.
    /// </summary>
    private void UpdateVoice(int channel, int segment, int keyMode, long sample)
    {
        FmVoice voice = _voices[channel];
        double wave = ReadSigned(Read(segment + SWavetable) + Read(segment + SLfoPhase));
        double fnum = (Read(segment + SFreqAcc) / 64.0)
            + (ReadSigned(segment + SLfoDepth) * wave / 65536.0);
        double attenuation = (ushort)(Read(segment + SAmpAcc) ^ 0x0FFF) / 64.0;
        voice.SetTarget(fnum, Read(segment + SOctCur) & 7, attenuation, sample, _rampSamples);
        if (keyMode == 0)
        {
            voice.KeyOff();
        }
        else if (keyMode == 2)
        {
            voice.KeyOn(retrigger: true);
            KeyOns++;
        }
        else if (keyMode == 1)
        {
            if (!voice.Active)
            {
                KeyOns++;
            }

            voice.KeyOn(retrigger: false);
        }
    }

    private void RecomputeOwned()
    {
        int mask = 0;
        for (int slot = 0; slot <= 6; slot += 2)
        {
            int block = Read(SlotArray + slot);
            if (block == 0)
            {
                continue;
            }

            int slotMask = Read(block + HSlotMask);
            for (int channel = 0; channel < MaxChannels; channel++)
            {
                if ((slotMask >> channel & 1) == 0)
                {
                    continue;
                }

                mask |= 1 << (channel <= 8 ? channel : channel == 9 ? 8 : 7);   // CYM→ch8, HH→ch7
            }
        }

        OwnedChannelMask = mask;
    }

    // ---------------------------------------------------------------- rendering

    /// <summary>
    /// Advances every voice one 48 kHz sample and returns their summed contribution (nominally
    /// −1..+1 before the mixer's scaling).
    /// </summary>
    /// <param name="sample">The output sample index, which drives the ramp interpolation.</param>
    public double RenderSample(long sample)
    {
        double accumulator = 0;
        for (int c = 0; c < MaxChannels; c++)
        {
            accumulator += _voices[c].Render(sample);
        }

        return accumulator;
    }
}

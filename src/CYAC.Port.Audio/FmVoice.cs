namespace CYAC.Port.Audio;

/// <summary>
/// One 2-operator FM voice in floating point — the clean-room OPL2 subset the generator engine
/// renders through.
/// </summary>
/// <remarks>
/// <para>
/// Per-operator ADSR, KSL, the four waveforms and self-feedback (platform: YM3812 register
/// semantics, from the public datasheet's register table — not ported from any emulator core), but
/// fed by CONTINUOUS parameters: the F-Number and the attenuation are linearly interpolated across
/// each 256 Hz driver tick instead of stepping, and the attenuation keeps its full 12-bit resolution
/// instead of being rounded into the chip's 6-bit TL field.  That, plus real key-off release tails,
/// is what "refined" means (item 2).
/// </para>
/// <para>
/// The per-voice half of the REFINED-SFX model — see <see cref="AdlToneEngine"/>'s banner for the
/// provenance.  The
/// envelope-rate curve is a smooth monotonic approximation, NOT the chip's nonlinear rate table
/// (hypothesis, tuned so rate 1 is ~1/3 s and rate 15 ~1 ms).
/// </para>
/// </remarks>
internal sealed class FmVoice
{
    /// <summary>The segment's embedded 12-byte FM patch record, as programmed.</summary>
    /// <remarks>
    /// Bytes 0..4 = MODULATOR registers <c>{0x20,0x40,0x60,0x80,0xE0}</c>, bytes 5..9 = CARRIER,
    /// byte 10 = register 0xC0, byte 11 = the rhythm BD merge byte.
    /// </remarks>
    public readonly byte[] Patch = new byte[12];

    /// <summary>
    /// A rhythm slot: the driver drives it through ONE operator offset.
    /// </summary>
    /// <remarks>
    /// <c>sub_043E</c>'s loop BREAKS at <c>di == 5</c> when <c>[0xB22] &gt;= 7</c>
    /// (<c>asset:1b/adldrive.drv@0x050A</c>), so exactly FIVE bytes — the MODULATOR half, patch
    /// bytes 0..4 — reach the chip through the single operator offset, and the live TL merge picks
    /// record byte 1, not 6.  A single-operator voice therefore reads from base 0.
    /// </remarks>
    public bool SingleOperator;

    /// <summary>Register 0xC0 bit 0: 1 = additive (both operators sound), 0 = FM.</summary>
    public bool AdditiveConnection;

    /// <summary>Whether the voice is keyed on.</summary>
    public bool Active;

    /// <summary>Register 0xC0 bits 1..3 — modulator self-feedback depth.</summary>
    public int Feedback;

    private double _fnum0, _fnum1, _atten0, _atten1;   // interpolation endpoints
    private long _anchor;
    private double _ramp = 1;
    private int _block;
    private double _phaseModulator, _phaseCarrier, _feedbackMemory;
    private double _envelopeModulator, _envelopeCarrier;
    private int _stageModulator = 4, _stageCarrier = 4;   // 0 attack 1 decay 2 sustain 3 release 4 off

    private static readonly double[] MultiplierTable =
        [0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 10, 12, 12, 15, 15];

    private static readonly double[] KeyScaleLevelDb = [0, 1.5, 3, 6];
    private static readonly double[] FeedbackScale = [0, 0.03, 0.06, 0.12, 0.25, 0.5, 1.0, 2.0];

    /// <summary>The FM phase-modulation index (tuned for character, not chip-calibrated).</summary>
    private const double ModulationIndex = 4.0;

    /// <summary>The per-voice output trim the emulator's mixer model applies.</summary>
    private const double VoiceTrim = 0.18;

    /// <summary>Aims the voice's ramps at a new (F-Number, attenuation) pair.</summary>
    /// <param name="fnum">The un-quantized F-Number.</param>
    /// <param name="block">The OPL block (octave) 0..7.</param>
    /// <param name="attenuation">The un-quantized attenuation, in 0.75 dB / 64 units.</param>
    /// <param name="sample">The sample the change lands at.</param>
    /// <param name="ramp">How many samples to reach the target over (one driver tick).</param>
    public void SetTarget(double fnum, int block, double attenuation, long sample, double ramp)
    {
        _fnum0 = CurrentFnum(sample);
        _atten0 = CurrentAttenuation(sample);
        _fnum1 = fnum;
        _atten1 = attenuation;
        _block = block;
        _anchor = sample;
        _ramp = ramp <= 1 ? 1 : ramp;
    }

    /// <summary>Keys the voice on; a retrigger restarts both envelopes' attack.</summary>
    /// <param name="retrigger">True for a re-attack, false for a legato key-hold.</param>
    public void KeyOn(bool retrigger)
    {
        Active = true;
        if (retrigger || _stageModulator == 4)
        {
            _stageModulator = 0;
            _stageCarrier = 0;
        }
    }

    /// <summary>Keys the voice off — both operators enter release.</summary>
    public void KeyOff()
    {
        Active = false;
        if (_stageModulator != 4)
        {
            _stageModulator = 3;
        }

        if (_stageCarrier != 4)
        {
            _stageCarrier = 3;
        }
    }

    /// <summary>Advances the voice one sample and returns its output.</summary>
    /// <param name="sample">The output sample index.</param>
    public double Render(long sample)
    {
        if (_stageModulator == 4 && _stageCarrier == 4)
        {
            return 0;
        }

        double fnum = CurrentFnum(sample);
        if (fnum <= 0)
        {
            return 0;
        }

        double channelFrequency = fnum * 49_716.0 / (1 << (20 - _block));
        double attenuation = CurrentAttenuation(sample);
        int carrier = SingleOperator ? 0 : 5;
        double sum = 0, modulatorOut = 0;

        if (!SingleOperator)
        {
            double envelope = StepEnvelope(
                ref _stageModulator,
                ref _envelopeModulator,
                Patch[2],
                Patch[3],
                (Patch[0] & 0x20) != 0,
                (Patch[0] & 0x10) != 0);
            double frequency = channelFrequency * MultiplierTable[Patch[0] & 0x0F];
            if (frequency is >= 20 and <= 22_000)
            {
                _phaseModulator += frequency / AudioFormat.SampleRate;
                if (_phaseModulator >= 1)
                {
                    _phaseModulator -= 1;
                }

                double feedback = Feedback > 0 ? _feedbackMemory * FeedbackScale[Feedback] : 0;
                double raw = Waveform(Patch[4] & 3, Fraction(_phaseModulator + feedback));

                // Connection 1 (additive) merges tl_cur into BOTH operators (sub_0515 drv @0x0487);
                // connection 0 (FM) leaves the modulator at the patch's own fixed TL, which is
                // exactly the fixed modulation index the sound designer chose.
                double modulatorAttenuation = AdditiveConnection ? attenuation : Patch[1] & 0x3F;
                modulatorOut = raw * envelope * Amplitude(modulatorAttenuation, Patch[1] >> 6);
                _feedbackMemory = (_feedbackMemory + modulatorOut) / 2;
                if (AdditiveConnection)
                {
                    sum += modulatorOut * VoiceTrim;
                }
            }
        }

        {
            double envelope = StepEnvelope(
                ref _stageCarrier,
                ref _envelopeCarrier,
                Patch[carrier + 2],
                Patch[carrier + 3],
                (Patch[carrier] & 0x20) != 0,
                (Patch[carrier] & 0x10) != 0);
            double frequency = channelFrequency * MultiplierTable[Patch[carrier] & 0x0F];
            if (frequency is >= 20 and <= 22_000)
            {
                _phaseCarrier += frequency / AudioFormat.SampleRate;
                if (_phaseCarrier >= 1)
                {
                    _phaseCarrier -= 1;
                }

                double phaseModulation =
                    !SingleOperator && !AdditiveConnection ? modulatorOut * ModulationIndex : 0;
                double raw = Waveform(Patch[carrier + 4] & 3, Fraction(_phaseCarrier + phaseModulation));
                sum += raw * envelope * Amplitude(attenuation, Patch[carrier + 1] >> 6) * VoiceTrim;
            }
        }

        return sum;
    }

    private double Lerp(double a, double b, long sample)
    {
        double t = (sample - _anchor) / _ramp;
        return t <= 0 ? a : t >= 1 ? b : a + ((b - a) * t);
    }

    private double CurrentFnum(long sample) => Lerp(_fnum0, _fnum1, sample);

    private double CurrentAttenuation(long sample) => Lerp(_atten0, _atten1, sample);

    /// <summary>The four OPL2 waveforms (platform: YM3812 datasheet shapes, closed forms).</summary>
    private static double Waveform(int select, double phase) => (select & 3) switch
    {
        0 => Math.Sin(phase * 2 * Math.PI),
        1 => phase < 0.5 ? Math.Sin(phase * 2 * Math.PI) : 0.0,
        2 => Math.Abs(Math.Sin(phase * 2 * Math.PI)),
        _ => phase < 0.25 ? Math.Sin(phase * 4 * Math.PI) : 0.0,
    };

    private static double Fraction(double x)
    {
        x %= 1.0;
        return x < 0 ? x + 1 : x;
    }

    private static double RateSeconds(int rate) =>
        rate <= 0 ? double.PositiveInfinity : 0.5 / Math.Pow(1.5, rate);

    private double StepEnvelope(
        ref int stage, ref double level, int reg60, int reg80, bool sustainEnable, bool keyScaleRate)
    {
        int boost = keyScaleRate ? _block : _block >> 2;
        switch (stage)
        {
            case 0:
            {
                int attack = Math.Min(15, (reg60 >> 4) + boost);
                if (attack == 0)
                {
                    break;                     // AR=0: never leaves attack (real HW: stays silent)
                }

                level = Math.Min(1.0, level + (1.0 / (RateSeconds(attack) * AudioFormat.SampleRate)));
                if (level >= 1.0)
                {
                    stage = 1;
                }

                break;
            }

            case 1:
            {
                int decay = Math.Min(15, (reg60 & 0xF) + boost);
                int sustainLevel = reg80 >> 4;

                // SL=15 is true maximum attenuation (platform: real HW quirk).
                double sustain = sustainLevel == 15 ? 0.0 : Math.Pow(10, -(sustainLevel * 3.0) / 20.0);
                double target = sustainEnable ? sustain : 0.0;
                if (decay == 0)
                {
                    break;
                }

                level = Math.Max(
                    target, level - (1.0 / (RateSeconds(decay) * AudioFormat.SampleRate)));
                if (level <= target + 1e-6)
                {
                    stage = sustainEnable ? 2 : 4;
                }

                break;
            }

            case 3:
            {
                int release = Math.Min(15, (reg80 & 0xF) + boost);
                if (release == 0)
                {
                    break;
                }

                level = Math.Max(0, level - (1.0 / (RateSeconds(release) * AudioFormat.SampleRate)));
                if (level <= 0.0001)
                {
                    level = 0;
                    stage = 4;
                }

                break;
            }

            case 4:
                level = 0;
                break;

            default:
                break;
        }

        return level;
    }

    /// <summary>
    /// Amplitude from a CONTINUOUS attenuation: the chip's TL field is 0.75 dB per step over 6 bits,
    /// so the driver's 12-bit <c>amp_accum</c> maps to 0.75/64 dB per unit — the resolution the
    /// register write throws away.
    /// </summary>
    private double Amplitude(double attenuation64, int keyScaleLevel) => Math.Pow(
        10,
        -((Math.Min(attenuation64, 63.0) * 0.75) + (KeyScaleLevelDb[keyScaleLevel] * _block)) / 20.0);
}

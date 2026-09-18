namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// The digitized-speech playback device — persisted at <c>yeager.cfg@0x0F</c>, live at
/// <c>g_speech_device_index [0xC30E]</c>.  Each value names a <c>.SP</c> driver asset.
/// </summary>
/// <remarks>
/// <para>
/// byte-verified speech DEVICE index:
/// the autodetect probe arms write 1/6/2/7 through <c>C6 06 0E C3 imm8</c> @<c>image@0x2D699</c> /
/// <c>0x2D6A7</c> / <c>0x2D6B5</c> / <c>0x2D6C3</c>, and the value is compared against 5 (Covox)
/// @<c>image@0x2D6C8</c> and 6 (TandyTL) @<c>image@0x25429</c>.  Source:
/// </para>
/// <para>
/// Value 4 has no assignment in the image; value 3 names an unshipped Tandy-extended bank.  The
/// enum lists exactly what the scanner documents and nothing else.
/// </para>
/// </remarks>
public enum SpeechDevice
{
    /// <summary>0 — no speech.</summary>
    None = 0,

    /// <summary>1 — <c>IBMSPKR.SP</c>, the PC speaker.  The shipped <c>yeager.cfg</c> default.</summary>
    PcSpeaker = 1,

    /// <summary>2 — <c>ADLIB.SP</c>.</summary>
    AdLib = 2,

    /// <summary>3 — the Tandy-extended bank; not shipped in any <c>.lib</c>.</summary>
    TandyExtended = 3,

    /// <summary>5 — <c>COVOX.SP</c>.</summary>
    Covox = 5,

    /// <summary>6 — <c>TANDYTL.SP</c>; also the value that suppresses the keyboard poll during speech.</summary>
    TandyTl = 6,

    /// <summary>7 — <c>BLASTER.SP</c>.</summary>
    SoundBlaster = 7,

    /// <summary>0xFF — "probe at boot": the reader's hardware autodetect phase resolves it.</summary>
    AutoDetect = 0xFF,
}

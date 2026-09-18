using CYAC.Port.Core.Data;

namespace CYAC.Port.Core.Sim;

/// <summary>
/// One key the game's keyboard layer produced — the word
/// <c>kbd_event_poll_and_classify @image@0x20488</c> returns in <c>AX</c>.
/// </summary>
/// <param name="Word">
/// The whole 16-bit word.  Either an ASCII character in <c>0x01..0xFF</c>, or a
/// <b>scancode-space</b> word <c>(scancode &lt;&lt; 8) | modifier</c> for the function and navigation
/// keys.  <c>0</c> is never produced — the classifier's <c>or ax,ax ; je</c>
/// (<c>image@0x204E0</c>) means "no key".
/// </param>
/// <param name="Scancode">The make scancode the word came from (bit 7 already stripped).</param>
/// <param name="Shift">Whether a shift key was held (<c>[0x32AC]</c>).</param>
/// <param name="Control">Whether a control key was held (<c>[0x32AE]</c>).</param>
public readonly record struct CookedKey(int Word, byte Scancode, bool Shift, bool Control)
{
    /// <summary>
    /// True for the function / navigation keys, which the classifier returns as
    /// <c>(scancode &lt;&lt; 8) | flag</c> rather than as a character
    /// (<c>cmp ah,0x3b ; jb</c> / <c>cmp ah,0x53 ; ja</c>, <c>image@0x204BF..0x204C7</c>).
    /// </summary>
    public bool IsScancodeSpace => (Word & 0xFF00) != 0;

    /// <summary>The character, when <see cref="IsScancodeSpace"/> is false.</summary>
    public int Character => Word & 0xFF;
}

/// <summary>
/// The game's keyboard cooking layer, from raw make/break scancodes to the word
/// <c>mission_state_machine</c>'s key ladder dispatches on:
/// <c>kbd_ring_dequeue_and_translate @image@0x296E4</c> followed by
/// <c>kbd_event_poll_and_classify @image@0x20488</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the port needs it.</b>  A <c>.evq</c> recording carries raw scancodes (make/break in bit
/// 7) because that is what the emulator injects into the game's own INT 9 ISR
/// (<c>custom_int09_keyboard_isr @image@0x295DA</c>).  Replaying a recording through the ported
/// flight kernel therefore needs the same translation the game performs — otherwise "the pilot
/// pressed <c>B</c>" cannot become "the airbrakes toggled".
/// </para>
/// <para>
/// <b>The 256-byte table is DATA and is not embedded here</b> (the no-original-data rule).  It is read
/// from the transformed data tree (<c>exe/tables/kbd_scancode_to_ascii.json</c>, produced from
/// <c>image@0x3501A</c>) via <see cref="FromDataTree"/>, or from a program image via
/// <see cref="FromProgramImage"/>.  Only the RULES are transliterated:
/// </para>
/// <list type="number">
/// <item>a break code (bit 7) never produces a key; it only clears a held-modifier flag —
/// <c>or bl,0x80</c> selects the release half of the per-key held-flag array
/// <c>[0x32B4..0x3333]</c>;</item>
/// <item>a shift key selects the table's upper half (<c>or bl,0x80</c> on the table index,
/// the modifier scancodes come from <c>g_kbd_modifier_key_table [0x3362]</c>, whose
/// first three groups are <c>2A,36</c> (shift), <c>38</c> (alt), <c>1D</c> (control)
/// @<c>0x29790</c>, and the standard AT set-1 assignment
/// (<c>platform</c>);</item>
/// <item>a scancode in <c>0x3B..0x53</c> is returned as <c>(scancode &lt;&lt; 8) | flag</c> with
/// <c>flag</c> = 1 for control, 2 for shift, else 0 (<c>image@0x204C9..0x204DF</c>);</item>
/// <item>otherwise the high byte is dropped (<c>xor ah,ah</c>, <c>image@0x204E4</c>) and, if
/// control is held and the character is <c>'a'..'z'</c>, <c>0x60</c> is subtracted
/// (<c>image@0x204ED..0x204F5</c>);</item>
/// <item>a word of zero means "no key" (<c>or ax,ax ; je</c>, <c>image@0x204E0</c>).</item>
/// </list>
/// <para>
/// <b>Deliberately not modelled:</b> the <c>E0</c>/<c>E1</c> prefix path and the 84-key normaliser,
/// which map into the synthetic scancode space <c>0x60..0x6F</c>.  Neither can reach a cockpit key:
/// <c>0x66..0x6F</c> is remapped back into <c>0x47..0x53</c> by the 10-byte table <c>[0x06B3]</c>
/// (<c>image@0x2049A..0x204A3</c>) and so lands in the scancode-space branch, and the table's
/// <c>0x60..0x65</c> entries are <c>ESC / Enter / 0 / '/' / 0 / 0</c>.  A prefix byte is counted
/// (<see cref="ExtendedPrefixesSeen"/>) rather than silently dropped.  The nine <c>det</c>
/// recordings this port verifies against contain <b>no</b> <c>E0</c> or <c>E1</c> byte at all.
/// </para>
/// </remarks>
public sealed class CockpitKeyTranslator
{
    /// <summary>The table's length: 256 — the unshifted half then the shifted half.</summary>
    public const int TableBytes = 256;

    /// <summary>The table's offset in the layer-1 image.</summary>
    public const int TableImageOffset = 0x3501A;

    /// <summary>The left shift key's scancode (<c>[0x3362]</c> group 0; <c>platform</c> AT set 1).</summary>
    public const byte LeftShiftScancode = 0x2A;

    /// <summary>The right shift key's scancode (<c>[0x3362]</c> group 0).</summary>
    public const byte RightShiftScancode = 0x36;

    /// <summary>The control key's scancode (<c>[0x3362]</c> group 2 → <c>[0x32AE]</c>).</summary>
    public const byte ControlScancode = 0x1D;

    /// <summary>The alt key's scancode (<c>[0x3362]</c> group 1 → <c>[0x32AD]</c>).</summary>
    public const byte AltScancode = 0x38;

    /// <summary>The first scancode the classifier returns in scancode space (<c>cmp ah,0x3b</c>).</summary>
    public const byte FirstScancodeSpaceCode = 0x3B;

    /// <summary>The last scancode the classifier returns in scancode space (<c>cmp ah,0x53</c>).</summary>
    public const byte LastScancodeSpaceCode = 0x53;

    private readonly byte[] _table;

    /// <summary>Builds a translator over the game's own 256-byte table.</summary>
    /// <param name="scancodeToAscii">
    /// The table, exactly <see cref="TableBytes"/> bytes: index <c>0x00..0x7F</c> unshifted,
    /// <c>0x80..0xFF</c> shifted.
    /// </param>
    /// <exception cref="ArgumentException">The table is not 256 bytes.</exception>
    public CockpitKeyTranslator(ReadOnlySpan<byte> scancodeToAscii)
    {
        if (scancodeToAscii.Length != TableBytes)
        {
            throw new ArgumentException(
                $"the scancode table is {TableBytes} bytes; got {scancodeToAscii.Length}",
                nameof(scancodeToAscii));
        }

        _table = scancodeToAscii.ToArray();
    }

    /// <summary>Whether a shift key is currently held (<c>[0x32AC]</c>).</summary>
    public bool ShiftHeld { get; private set; }

    /// <summary>Whether the control key is currently held (<c>[0x32AE]</c>).</summary>
    public bool ControlHeld { get; private set; }

    /// <summary>
    /// Whether the alt key is currently held (<c>[0x32AD]</c>).  Nothing on the cockpit path reads
    /// it — the classifier uses it only for the Ctrl+Alt+Del reboot (<c>image@0x204AA</c>) — but the
    /// port tracks it so the modifier state is complete.
    /// </summary>
    public bool AltHeld { get; private set; }

    /// <summary>How many <c>E0</c>/<c>E1</c> prefix bytes the stream contained (see the remarks).</summary>
    public int ExtendedPrefixesSeen { get; private set; }

    /// <summary>Reads the table out of the transformed data tree.</summary>
    /// <param name="tree">An open data tree.</param>
    /// <exception cref="InvalidDataException">The document does not carry 256 entries.</exception>
    public static CockpitKeyTranslator FromDataTree(DataTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ScancodeTableDto document = tree.ScancodeToAscii;
        if (document.Entries is not { } entries || entries.Count != TableBytes)
        {
            throw new InvalidDataException(
                $"exe/tables/kbd_scancode_to_ascii.json carries {document.Entries?.Count ?? 0} "
                    + $"entries; the table is {TableBytes} bytes.");
        }

        byte[] table = new byte[TableBytes];
        for (int i = 0; i < TableBytes; i++)
        {
            table[i] = unchecked((byte)entries[i].Ascii);
        }

        return new CockpitKeyTranslator(table);
    }

    /// <summary>Slices the table out of an unpacked layer-1 program image.</summary>
    /// <param name="image">The image (or the tree's copy).</param>
    /// <remarks>
    /// TEST-ONLY: the running port builds the translator from <c>FromDataTree</c>, and
    /// this overload exists so <c>CockpitKeyTranslatorTests</c> can prove the document and the shipped
    /// image agree.
    /// </remarks>
    public static CockpitKeyTranslator FromProgramImage(ReadOnlySpan<byte> image) =>
        new(image.Slice(TableImageOffset, TableBytes));

    /// <summary>Forgets the held modifiers — what a scene change does to <c>[0x32AC..0x32B1]</c>.</summary>
    public void Reset()
    {
        ShiftHeld = false;
        ControlHeld = false;
        AltHeld = false;
    }

    /// <summary>
    /// Feeds one raw scancode byte in and returns the key it cooked to, or <see langword="null"/>
    /// when the byte produced no key (a break, a modifier, a prefix, or a key with no character).
    /// </summary>
    /// <param name="raw">The byte as the ISR saw it: make code, break flag in bit 7.</param>
    public CookedKey? Translate(byte raw)
    {
        if (raw is 0xE0 or 0xE1)
        {
            ExtendedPrefixesSeen++;
            return null;
        }

        bool release = (raw & 0x80) != 0;
        byte code = (byte)(raw & 0x7F);

        switch (code)
        {
            case LeftShiftScancode:
            case RightShiftScancode:
                ShiftHeld = !release;
                return null;
            case ControlScancode:
                ControlHeld = !release;
                return null;
            case AltScancode:
                AltHeld = !release;
                return null;
            default:
                break;
        }

        if (release)
        {
            return null;
        }

        // image@0x204BF..0x204DF — the function / navigation keys never become characters.
        if (code is >= FirstScancodeSpaceCode and <= LastScancodeSpaceCode)
        {
            int flag = ControlHeld ? 1 : ShiftHeld ? 2 : 0;
            return new CookedKey((code << 8) | flag, code, ShiftHeld, ControlHeld);
        }

        // The table lookup itself: `or bl,0x80` picks the shifted half.
        int character = _table[ShiftHeld ? code | 0x80 : code];
        if (character == 0)
        {
            return null;                                   // image@0x204E0: or ax,ax ; je
        }

        if (ControlHeld && character is >= 'a' and <= 'z')
        {
            character -= 0x60;                             // image@0x204F5: sub al,0x60
        }

        return new CookedKey(character, code, ShiftHeld, ControlHeld);
    }
}

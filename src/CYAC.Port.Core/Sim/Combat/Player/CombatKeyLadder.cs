namespace CYAC.Port.Core.Sim.Combat.Player;

/// <summary>
/// <c>mission_state_machine</c>'s IN-FLIGHT KEY LADDER — the 79-arm <c>dec ax / sub ax,imm / jne +3
/// / jmp handler</c> chain at <c>image@0x00E27..0x01071</c>, decoded from the bytes.
/// </summary>
/// <remarks>
/// <para>
/// The ladder reads its cooked key in exactly ONE place, <c>mov ax,[bp-0xA]</c> at
/// <c>image@0x00E27</c> (trace probe P30 from format v1.6), and then walks a chain of decrements:
/// each step subtracts 1 (<c>dec ax</c>) or a small immediate (<c>sub ax,imm</c>) and tests <c>jne
/// +3 / jmp handler</c>, so the key that reaches a given handler is the RUNNING SUM of every
/// decrement up to and including its own test.
/// </para>
/// <para>
/// A correction to, which counted only <c>dec ax</c> and so mis-attributed the eleven <c>sub
/// ax,imm</c> steps: it reported the chaff arm at key <c>0x18</c> and printed duplicate keys.  The
/// chain has 79 arms and its keys run <c>0x01..0x5101</c>;
/// </para>
/// <para>
/// A key that matches NO arm falls out of the chain at <c>image@0x0105F</c>, where
/// <c>cmp byte [0xC31C],0 / jne</c> either drops it (jumping straight back to the loop top
/// <c>image@0x00E08</c>) or pushes it into
/// <c>cockpit_key_dispatch_gated @image@0x2257C</c> (<c>lcall 0x324c:0xbc</c> @<c>image@0x0106C</c>)
/// — the cockpit channel.
/// </para>
/// <para>
/// 18 of the 79 handlers open with the same in-flight gate <c>cmp byte [0xC31C],0 / jne +3 /
/// jmp image@0x012FF</c> (which is a bare <c>jmp image@0x00E08</c>, the loop top), so those keys do
/// NOTHING while the key gate is closed.  The two the combat kernel owns are both in that set.
/// </para>
/// </remarks>
public static class CombatKeyLadder
{
    /// <summary>
    /// The cooked key that reaches <c>chaff_fire @image@0x0AF68</c> — handler
    /// <c>image@0x0123E</c>, whose <c>lcall 0x108e:0xa688</c> at <c>image@0x01248</c> resolves to
    /// <c>0x108E0 + 0xA688 = image@0x0AF68</c>.
    /// </summary>
    public const int ChaffKey = 0x39;

    /// <summary>
    /// The cooked key that reaches <c>flare_fire @image@0x0AFCC</c> — handler
    /// <c>image@0x01250</c>, <c>lcall 0x108e:0xa6ec</c> at <c>image@0x0125A</c>.
    /// </summary>
    public const int FlareKey = 0x30;

    /// <summary>
    /// The cooked key that opens the LOCK-ON's list mode — handler <c>image@0x01229</c>, which
    /// sets <c>[0x00BB] = [0x00BA] = 1</c> (<c>mov al,1 / mov [0xbb],al / mov [0xba],al</c>
    /// @<c>image@0x01233..0x0123A</c>).  Those are exactly the two bytes
    /// <see cref="PlayerTargetLock"/> branches on, so this key is what a recording must press to
    /// light the free-camera selector.
    /// </summary>
    public const int LockOnListModeKey = 0x27;

    /// <summary>The in-flight key gate every gated handler tests — <c>[0xC31C]</c>.</summary>
    public const int InFlightKeyGate = 0xC31C;

    /// <summary>The ladder's fall-through door — <c>image@0x0106C</c>.</summary>
    public const int CockpitDispatchDoor = 0x2257C;

    /// <summary>One arm of the chain.</summary>
    /// <param name="CookedKey">The cooked key the chain's running sum matches.</param>
    /// <param name="Handler">The handler's image offset.</param>
    /// <param name="InFlightGated">
    /// Whether the handler opens with <c>cmp byte [0xC31C],0 / jne +3 / jmp image@0x012FF</c>.
    /// </param>
    public readonly record struct Arm(int CookedKey, int Handler, bool InFlightGated);

    /// <summary>The 79 arms, in chain order.</summary>
    public static ReadOnlySpan<Arm> LadderArms => ladderArms;

    private static readonly Arm[] ladderArms =
    [
        new(0x0001, 0x012A7, true),
        new(0x0002, 0x011C8, false),
        new(0x0003, 0x01102, false),
        new(0x0005, 0x011D2, false),
        new(0x0006, 0x01356, true),
        new(0x0008, 0x013D8, true),
        new(0x0009, 0x0118E, false),
        new(0x000C, 0x01198, false),
        new(0x000D, 0x01212, true),
        new(0x0010, 0x01150, false),
        new(0x0011, 0x00B5C, false),
        new(0x0012, 0x012DC, true),
        new(0x0013, 0x0112D, false),
        new(0x0014, 0x011DC, false),
        new(0x0015, 0x011A2, false),
        new(0x001A, 0x01294, true),
        new(0x001B, 0x010CD, false),
        new(0x0021, 0x0154B, false),
        new(0x0023, 0x0155B, false),
        new(0x0024, 0x01563, false),
        new(0x0027, 0x01229, true),
        new(0x002B, 0x01543, false),
        new(0x002C, 0x012C7, true),
        new(0x002D, 0x0153B, false),
        new(0x002E, 0x012B2, true),
        new(0x002F, 0x014FD, false),
        new(0x0030, 0x01250, true),
        new(0x0039, 0x0123E, true),
        new(0x003D, 0x01543, false),
        new(0x0040, 0x01553, false),
        new(0x0045, 0x012F8, true),
        new(0x0054, 0x01166, false),
        new(0x0057, 0x01285, true),
        new(0x005B, 0x011EA, true),
        new(0x005D, 0x01201, true),
        new(0x005F, 0x0153B, false),
        new(0x0064, 0x012E9, true),
        new(0x0070, 0x0117C, false),
        new(0x0072, 0x01262, true),
        new(0x0074, 0x01166, false),
        new(0x0077, 0x01273, true),
        new(0x3B00, 0x0136F, false),
        new(0x3B02, 0x013A0, false),
        new(0x3C00, 0x01373, false),
        new(0x3C02, 0x013A5, false),
        new(0x3D00, 0x01378, false),
        new(0x3D02, 0x013AA, false),
        new(0x3E00, 0x0137D, false),
        new(0x3E02, 0x013AF, false),
        new(0x3F00, 0x01382, false),
        new(0x3F02, 0x013B4, false),
        new(0x4000, 0x01387, false),
        new(0x4002, 0x013B9, false),
        new(0x4100, 0x0138C, false),
        new(0x4102, 0x013BE, false),
        new(0x4200, 0x01391, false),
        new(0x4202, 0x013C3, false),
        new(0x4300, 0x01396, false),
        new(0x4302, 0x013C8, false),
        new(0x4400, 0x0139B, false),
        new(0x4402, 0x013CD, false),
        new(0x4700, 0x014E9, false),
        new(0x4800, 0x0146D, false),
        new(0x4801, 0x01479, false),
        new(0x4900, 0x014F1, false),
        new(0x4901, 0x0151F, false),
        new(0x4A00, 0x0153B, false),
        new(0x4B00, 0x013EE, false),
        new(0x4B01, 0x013FA, false),
        new(0x4C00, 0x014FD, false),
        new(0x4C01, 0x01514, false),
        new(0x4D00, 0x0142E, false),
        new(0x4D01, 0x0143A, false),
        new(0x4E00, 0x01543, false),
        new(0x4F00, 0x014ED, false),
        new(0x5000, 0x014AA, false),
        new(0x5001, 0x014B6, false),
        new(0x5100, 0x014F5, false),
        new(0x5101, 0x0152D, false),
    ];

    /// <summary>The arm a cooked key reaches, or null when it falls through the whole chain.</summary>
    /// <param name="cookedKey">The cooked key word.</param>
    /// <returns>The arm, or <see langword="null"/>.</returns>
    public static Arm? ArmFor(int cookedKey)
    {
        foreach (Arm arm in ladderArms)
        {
            if (arm.CookedKey == cookedKey)
            {
                return arm;
            }
        }

        return null;
    }

    /// <summary>Which kernel owns the arm a cooked key reaches.</summary>
    /// <param name="cookedKey">The cooked key word.</param>
    /// <returns>The ladder arm class.</returns>
    public static CombatLadderArm Classify(int cookedKey) => cookedKey switch
    {
        ChaffKey => CombatLadderArm.Chaff,
        FlareKey => CombatLadderArm.Flare,
        _ => ArmFor(cookedKey) is null ? CombatLadderArm.Cockpit : CombatLadderArm.External,
    };

    /// <summary>
    /// A NAME for a key the port does not model — the attribution string the driver uses, with the
    /// key code and its handler in it, so "unmodelled" is a list of addresses rather than a feeling.
    /// </summary>
    /// <param name="cookedKey">The cooked key word.</param>
    /// <returns>The reason string.</returns>
    public static string AttributionFor(int cookedKey) => ArmFor(cookedKey) is { } arm
        ? $"the key ladder's unmodelled arm for cooked key 0x{cookedKey:X4} "
            + $"(handler image@0x{arm.Handler:X5})"
        : $"the key ladder's fall-through for cooked key 0x{cookedKey:X4} "
            + $"(cockpit_key_dispatch_gated @image@0x{CockpitDispatchDoor:X5})";
}

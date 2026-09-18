using System.Globalization;

namespace CYAC.Port.Transform.Families;

/// <summary>
/// The port's own words for ids the game never names in text: the reading aids of
/// <c>scenarios.json</c> and the mission vocabulary print them beside the id.
/// </summary>
/// <remarks>
/// <para>
/// The game draws these ids as pictures or dots, or not at all, so there is nothing to read from the
/// originals (the designations it does name come from <see cref="OriginalNames"/>).  The words are
/// ours: an era's usual short name, the side each insignia frame belongs to, the manual's reading of
/// the rating dots, and the two class-table sentinels.  They are labels for readers; a document read
/// back ignores them.
/// </para>
/// </remarks>
public static class ReadingAidLabels
{
    /// <summary>The class-table id of the player's start (<c>0xFFFF</c> in the table).</summary>
    public const int PlayerClassId = 0;

    /// <summary>The class-table id of the home-base reference (<c>0xFFFE</c> in the table).</summary>
    public const int HomeBaseClassId = 1;

    /// <summary>Our word for <see cref="PlayerClassId"/>.</summary>
    public const string PlayerClass = "PLAYER";

    /// <summary>Our word for <see cref="HomeBaseClassId"/>.</summary>
    public const string HomeBaseClass = "HOME-BASE-POS";

    /// <summary>Our word for a mission with no featured opponent (<c>scenario.bin</c> <c>+0x04</c> = 0).</summary>
    public const string NoOpponent = "(none)";

    /// <summary>
    /// The three eras, by <c>scenario.bin</c>'s era byte (the theater each loads is
    /// <c>SDataModel.TheaterAssetForEra</c>).  The conflict screen draws its own longer captions.
    /// </summary>
    public static IReadOnlyList<string> Eras { get; } = ["WWII", "Korea", "Vietnam"];

    /// <summary>
    /// The side each frame of the four-frame insignia atlas belongs to, as P1 inferred it from the
    /// stat blocks' insignia byte and P2 checked against the drawn frames.
    /// </summary>
    public static IReadOnlyList<string> Insignia { get; } =
        ["USAAF/USAF", "Luftwaffe", "Soviet/N.Korean", "N.Vietnamese"];

    /// <summary>The manual's reading of the one to three rating dots, by <c>scenario.bin</c>'s rating byte.</summary>
    public static IReadOnlyDictionary<int, string> DifficultyRatings { get; } = new Dictionary<int, string>
    {
        [1] = "easy",
        [2] = "moderate",
        [3] = "especially difficult",
    };

    /// <summary>What a reading aid prints for an id it has no word for.</summary>
    /// <param name="id">The id.</param>
    public static string Unknown(int id) => string.Create(CultureInfo.InvariantCulture, $"?({id})");

    /// <summary>An era's label, or <see cref="Unknown"/>.</summary>
    /// <param name="era">The era byte.</param>
    public static string Era(int era) => era >= 0 && era < Eras.Count ? Eras[era] : Unknown(era);

    /// <summary>An insignia frame's label, or <see cref="Unknown"/>.</summary>
    /// <param name="frame">The insignia index.</param>
    public static string InsigniaFrame(int frame) =>
        frame >= 0 && frame < Insignia.Count ? Insignia[frame] : Unknown(frame);

    /// <summary>A rating's label, or <see cref="Unknown"/>.</summary>
    /// <param name="rating">The rating byte.</param>
    public static string DifficultyRating(int rating) =>
        DifficultyRatings.TryGetValue(rating, out string? label) ? label : Unknown(rating);
}

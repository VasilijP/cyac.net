namespace CYAC.Port.Core.Model.Mission;

/// <summary>
/// A header-level directive in a mission's tag stream: a setting that applies to the whole container
/// rather than to an object.
/// </summary>
/// <remarks>
/// The engine's directive cascade is <c>image@0x099D0..0x09A3C</c>; each tag writes a DGROUP global and
/// loops straight back to the tag reader without opening an object.  The names are the authoring
/// vocabulary of <c>CYAC.Formats.EaLib.SDataModel.DirectiveNames</c>; <see cref="MissionDefinition"/>
/// surfaces the ones with settled meanings as typed properties, and keeps the full ordered list here
/// so nothing is lost.
/// </remarks>
/// <param name="Name">The authoring name, e.g. <c>player_aircraft</c>.</param>
/// <param name="Tag">The stream tag byte, e.g. <c>0x95</c>.</param>
/// <param name="Value">The scalar operand (0 for operand-less directives).</param>
/// <param name="Coords">The four world-unit coordinates of <c>world_extents</c>, else <see langword="null"/>.</param>
public sealed record MissionDirective(string Name, byte Tag, int Value, IReadOnlyList<int>? Coords);

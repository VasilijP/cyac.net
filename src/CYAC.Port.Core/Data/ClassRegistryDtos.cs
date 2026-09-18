using System.Text.Json.Serialization;

namespace CYAC.Port.Core.Data;

// Wire format of <data>/exe/classes.json — the 23 world-object class records the shipping game
// defines (the original `s_mesh_registry_slot`).  Written by cyac-transform's `exe-tables` family,
// read by CYAC.Port.Core.Model.World.ClassRegistry.Load.
//
// Law L1: this file describes the SHAPE.  The 23 records themselves are extracted from the image
// into the data tree and never appear as literals in this source tree.

/// <summary>A class record's render descriptor — the original 14-byte <c>s_class_render_desc</c>.</summary>
public sealed class ClassRenderDescriptorDto
{
    /// <summary>The descriptor's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>Where the descriptor sits relative to the record: <c>0x50</c>, or <c>0x60</c> for the two terrain classes.</summary>
    [JsonPropertyName("offsetInRecord")]
    public string? OffsetInRecord { get; init; }

    /// <summary><c>+0x00</c> — the kind byte.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary><c>+0x01</c> — how many entries the element list holds.</summary>
    [JsonPropertyName("elementCount")]
    public int ElementCount { get; init; }

    /// <summary><c>+0x02</c> — the prepare callback; NULL on disk for most classes.</summary>
    [JsonPropertyName("prepareCallback")]
    public FarPointerDto? PrepareCallback { get; init; }

    /// <summary><c>+0x06</c> — the draw callback; NULL on disk in all 23 (runtime-patched with mesh-JIT code).</summary>
    [JsonPropertyName("drawCallback")]
    public FarPointerDto? DrawCallback { get; init; }

    /// <summary><c>+0x0A</c> — near pointer to the element list.</summary>
    [JsonPropertyName("elementList")]
    public string? ElementList { get; init; }

    /// <summary><c>+0x0C</c> — the flags word; bit9 is the draw enable.</summary>
    [JsonPropertyName("flags")]
    public string? Flags { get; init; }
}

/// <summary>A class's axis-aligned bounding box in world units.</summary>
public sealed class ClassBoundsDto
{
    /// <summary><c>+0x30</c> — minimum X.</summary>
    [JsonPropertyName("minX")]
    public int MinX { get; init; }

    /// <summary><c>+0x34</c> — maximum X.</summary>
    [JsonPropertyName("maxX")]
    public int MaxX { get; init; }

    /// <summary><c>+0x38</c> — minimum Y (negative = below the origin; Y is up).</summary>
    [JsonPropertyName("minY")]
    public int MinY { get; init; }

    /// <summary><c>+0x3C</c> — maximum Y.</summary>
    [JsonPropertyName("maxY")]
    public int MaxY { get; init; }

    /// <summary><c>+0x40</c> — minimum Z.</summary>
    [JsonPropertyName("minZ")]
    public int MinZ { get; init; }

    /// <summary><c>+0x44</c> — maximum Z.</summary>
    [JsonPropertyName("maxZ")]
    public int MaxZ { get; init; }
}

/// <summary>One world-object class record as the data tree carries it.</summary>
public sealed class ClassRecordDto
{
    /// <summary>The class's own lowercase basename, from the string <c>+0x22</c> points at.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The record's DGROUP offset.</summary>
    [JsonPropertyName("dgroup")]
    public string? Dgroup { get; init; }

    /// <summary>The record's <c>image@</c> offset.</summary>
    [JsonPropertyName("image")]
    public string? Image { get; init; }

    /// <summary>How many bytes the round trip covers: the header plus its render descriptor.</summary>
    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    /// <summary>
    /// False for a record the class-census predicate (<c>byte[+0] == 0x80</c>) did not surface — the
    /// <c>crater</c> record, whose priority is <c>0x1E</c>.
    /// </summary>
    [JsonPropertyName("censusMember")]
    public bool CensusMember { get; init; }

    /// <summary><c>+0x00</c> — painter's-order key; bit7 marks a distance-sorted true-3D object.</summary>
    [JsonPropertyName("renderLayerPriority")]
    public string? RenderLayerPriority { get; init; }

    /// <summary><c>+0x01</c> — the LOD/detail flags byte.</summary>
    [JsonPropertyName("flags")]
    public string? Flags { get; init; }

    /// <summary><c>+0x02</c> — the mesh-space extent.</summary>
    [JsonPropertyName("meshExtent")]
    public int MeshExtent { get; init; }

    /// <summary><c>+0x08</c> — the world-space extent, i.e. the mesh extent pre-shifted.</summary>
    [JsonPropertyName("rawExtent")]
    public int RawExtent { get; init; }

    /// <summary><c>+0x0C</c> — render-scale exponent; the mesh renders at <c>2^exp x</c> world scale.</summary>
    [JsonPropertyName("scaleShiftExponent")]
    public int ScaleShiftExponent { get; init; }

    /// <summary><c>+0x0E/+0x10/+0x12</c> — the three LOD switch thresholds.</summary>
    [JsonPropertyName("lodThresholds")]
    public List<int>? LodThresholds { get; init; }

    /// <summary><c>+0x16</c> — near pointer to the LOD-1 face descriptor; 0 when single-LOD.</summary>
    [JsonPropertyName("lodFaceDescriptor1")]
    public string? LodFaceDescriptor1 { get; init; }

    /// <summary><c>+0x18</c> — near pointer to the LOD-2 face descriptor; 0 when absent.</summary>
    [JsonPropertyName("lodFaceDescriptor2")]
    public string? LodFaceDescriptor2 { get; init; }

    /// <summary><c>+0x1A</c> — the class mesh's vertex count.</summary>
    [JsonPropertyName("vertexCount")]
    public int VertexCount { get; init; }

    /// <summary><c>+0x22</c> — near pointer to the basename string.</summary>
    [JsonPropertyName("namePointer")]
    public string? NamePointer { get; init; }

    /// <summary><c>+0x2C</c> — ground clearance; equals <c>-minY</c> in all 23 shipped records.</summary>
    [JsonPropertyName("groundClearance")]
    public int GroundClearance { get; init; }

    /// <summary><c>+0x2E</c> — the filter word OR-ed into a pool entry's flags on insertion.</summary>
    [JsonPropertyName("poolFilterWord")]
    public string? PoolFilterWord { get; init; }

    /// <summary><c>+0x30..+0x47</c> — the class's bounding box.</summary>
    [JsonPropertyName("bounds")]
    public ClassBoundsDto? Bounds { get; init; }

    /// <summary><c>+0x48</c> — a second face-descriptor near pointer; non-zero only for the two terrain classes.</summary>
    [JsonPropertyName("secondaryFaceDescriptor")]
    public string? SecondaryFaceDescriptor { get; init; }

    /// <summary>The record's render descriptor, from <c>+0x14</c>.</summary>
    [JsonPropertyName("renderDescriptor")]
    public ClassRenderDescriptorDto? RenderDescriptor { get; init; }

    /// <summary>
    /// <c>+0x0D</c> — one byte with no identified reader: <c>0x19</c> on the six fighters,
    /// <c>0x32</c> on <c>b17</c>, <c>0x0C</c> on the two ejection-seat classes, 0 elsewhere.
    /// </summary>
    [JsonPropertyName("unknown_0x0D")]
    public string? Unknown0x0D { get; init; }

    /// <summary>
    /// <c>+0x4A..+0x4F</c> — three words that repeat <c>+0x0E/+0x10/+0x12</c> exactly in all 23
    /// records.  A duplicate is an observation, not a meaning: no reader is identified, so the span
    /// is carried and counted.
    /// </summary>
    [JsonPropertyName("unknown_0x4A")]
    public string? Unknown0x4A { get; init; }

    /// <summary>
    /// <c>+0x50..+0x5F</c> — the 16-byte block <c>+0x48</c> points at on <c>mount2</c> and
    /// <c>mountain</c>, which displaces their render descriptor to <c>+0x60</c>.  It is NOT another
    /// <c>s_class_render_desc</c> (its shape does not match); absent on the other 21 records.
    /// </summary>
    [JsonPropertyName("unknown_0x50")]
    public string? Unknown0x50 { get; init; }
}

/// <summary>The whole class registry as the data tree carries it.</summary>
public sealed class ClassRegistryDocumentDto
{
    /// <summary>Document kind and version.</summary>
    [JsonPropertyName("format")]
    public string? Format { get; init; }

    /// <summary>What the records are, where they live and what is still open.</summary>
    [JsonPropertyName("about")]
    public string? About { get; init; }

    /// <summary>The DGROUP base every <c>dgroup</c> field is relative to: <c>image@0x3BD60</c>.</summary>
    [JsonPropertyName("dgroupImageBase")]
    public string? DgroupImageBase { get; init; }

    /// <summary>The class records, census members first, in DGROUP order.</summary>
    [JsonPropertyName("classes")]
    public List<ClassRecordDto>? Classes { get; init; }
}

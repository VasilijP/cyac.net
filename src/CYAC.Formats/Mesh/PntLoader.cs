// ---------------------------------------------------------------------------
// Moved from
// src/CYAC.Tools.ResourceBrowser/Mesh/ into the shared CYAC.Formats library —
// the pilot-9 refactor precedent (the EALIB decoders moved the same way).
// This file is PURE DECODE: no Avalonia, no raster, no viewer state.  The
// browser keeps the viewer/rasteriser (MeshRenderer / SoftRaster / MeshViewport
// / MeshViewerPanel / Camera / AxisConfig / Vec3 / VgaPalette / ImageAddress)
// and now references this namespace for the decode.  The mission editor's map
// uses it for the theater SCENERY FOOTPRINTS (SceneryFootprint.cs).
// Behaviour is byte-identical to the browser version — the move is mechanical
// (namespace only), and `--selftest-*` on both tools is the proof.
//
// The file-system lookup (FindPntPath / the resources/ walk-up / TryLoad) left
// this library with the development tools.  What stays is the decode (Parse),
// the PntLookup a decoder asks, and a lookup over archives the caller has
// already opened.
// ---------------------------------------------------------------------------
using CYAC.Formats.EaLib;

namespace CYAC.Formats.Mesh;

// .PNT file loader.
//
// .PNT files are EALIB members named <BASENAME>.PNT (every one of them in
// 1a.lib) and are loaded by the pnt_load_and_prerender path. Each is:
//
//   header (28 B):
//     +0..2   flag byte for each of 3 LOD slots
//     +3      zero
//     +4..9   3 × u16 LE = vert-section offsets for slots 0,1,2
//     +0xA..F 3 × u16 LE = poly-section offsets for slots 0,1,2
//     +0x10..15 3 × u16 LE = sub-section offsets for slots 0,1,2
//     +0x16..1B padding zeros
//
//   body: tightly-packed sections referenced by header offsets:
//     - vertex section: (i16 x, i16 y, i16 z) triplets, 6 B each
//     - poly section: per-vertex parent index (edge tree; byte[i]=parent of
//       vertex i, 0xFF = root)
//     - sub section: similar edge-tree continuation for LOD branching
//
// Parse decodes one member's bytes; the mesh decoder splices the per-LOD
// vertex arrays into its MeshRecords.

/// <summary>Supplies the decoded .PNT of a mesh basename, or null when the caller has none.</summary>
/// <param name="basename">The mesh's basename, e.g. <c>"hangar"</c>; any case.</param>
public delegate PntFile? PntLookup(string basename);

public sealed class PntFile
{
    /// <summary>Where the bytes came from, for display (a member name, or whatever the caller said).</summary>
    public required string Path { get; init; }
    public required string Basename { get; init; }
    public required byte[] FlagBytes { get; init; }      // [0..2] LOD flag bytes
    public required Vec3i[][] LodVertices { get; init; } // per-LOD vertex arrays (0..2)
    public required byte[][] LodPolyBytes { get; init; } // per-LOD poly bytes (edge tree)
    public required byte[][] LodSubBytes { get; init; }  // per-LOD sub-section bytes

    public bool HasLod(int lod) => LodVertices.Length > lod && LodVertices[lod].Length > 0;
    public Vec3i[] BestLod()
    {
        // Highest-detail LOD = highest LOD index that is populated.
        for (int i = LodVertices.Length - 1; i >= 0; i--)
            if (LodVertices[i].Length > 0) return LodVertices[i];
        return Array.Empty<Vec3i>();
    }
}

public static class PntLoader
{
    /// <summary>The archive member name a basename's .PNT is stored under: <c>HANGAR.PNT</c>.</summary>
    /// <param name="basename">The mesh basename, with or without the extension, in any case.</param>
    public static string MemberName(string basename)
    {
        ArgumentNullException.ThrowIfNull(basename);
        string name = basename.ToUpperInvariant();
        return name.EndsWith(".PNT", StringComparison.Ordinal) ? name : name + ".PNT";
    }

    /// <summary>
    /// A lookup over archives the caller has already opened: the first archive, in the order given,
    /// that holds <see cref="MemberName"/> answers with that member's decoded body.
    /// </summary>
    /// <param name="archives">The archives to search.</param>
    /// <returns>The lookup; it answers null for a basename no archive holds.</returns>
    /// <remarks>
    /// A member that is found but does not decode is an error, not a miss: the exception propagates.
    /// </remarks>
    public static PntLookup FromArchives(IEnumerable<EaLibArchive> archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        List<EaLibArchive> list = archives.ToList();
        foreach (EaLibArchive archive in list)
            ArgumentNullException.ThrowIfNull(archive, nameof(archives));

        return basename =>
        {
            string member = MemberName(basename);
            foreach (EaLibArchive archive in list)
            {
                if (archive.FindEntry(member) is { } entry)
                    return Parse(entry.GetDecoded(), basename, $"{archive.ShortName}/{entry.Name}");
            }

            return null;
        };
    }

    // The resources/ walk-up moved to the development tools; a published codec takes its inputs
    // from the caller.

    // ---------------------------------------------------------------------
    // BYTE-TAKING ENTRY POINT.
    //
    // The decode, with no file I/O: `cyac-transform` feeds the EALIB member's
    // DECODED body straight in (its container layer has already undone the
    // LZSS), so the transform never walks the file system for a resource. The
    // development tools' own lookup delegates here, so every caller shares
    // one implementation.
    // ---------------------------------------------------------------------

    /// <summary>Decodes a .PNT body. Returns null when it is too short to carry a header.</summary>
    /// <param name="data">The decoded .PNT bytes.</param>
    /// <param name="basename">The mesh basename this file belongs to.</param>
    /// <param name="path">Where the bytes came from, for display; optional.</param>
    public static PntFile? Parse(byte[] data, string basename, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(basename);
        {
            if (data.Length < 28) return null;

            byte[] flags = new byte[] { data[0], data[1], data[2] };
            int[] vertsOffs = new int[3];
            int[] polysOffs = new int[3];
            int[] subsOffs = new int[3];
            for (int i = 0; i < 3; i++)
            {
                vertsOffs[i] = ReadU16(data, 4 + i * 2);
                polysOffs[i] = ReadU16(data, 0xA + i * 2);
                subsOffs[i] = ReadU16(data, 0x10 + i * 2);
            }

            // All non-zero offsets + file end give us section spans.
            List<int> allOffs = new List<int> { data.Length };
            foreach (int v in vertsOffs) if (v != 0) allOffs.Add(v);
            foreach (int p in polysOffs) if (p != 0) allOffs.Add(p);
            foreach (int s in subsOffs) if (s != 0) allOffs.Add(s);
            allOffs.Sort();
            List<int> distinctOffs = allOffs.Distinct().ToList();

            int NextOff(int a)
            {
                foreach (int o in distinctOffs)
                    if (o > a) return o;
                return data.Length;
            }

            Vec3i[][] lodVerts = new Vec3i[3][];
            byte[][] lodPolys = new byte[3][];
            byte[][] lodSubs = new byte[3][];
            for (int i = 0; i < 3; i++)
            {
                if (vertsOffs[i] != 0)
                {
                    int a = vertsOffs[i];
                    int b = NextOff(a);
                    int nverts = (b - a) / 6;
                    Vec3i[] verts = new Vec3i[nverts];
                    for (int k = 0; k < nverts; k++)
                    {
                        short x = (short)ReadU16(data, a + k * 6 + 0);
                        short y = (short)ReadU16(data, a + k * 6 + 2);
                        short z = (short)ReadU16(data, a + k * 6 + 4);
                        verts[k] = new Vec3i(x, y, z);
                    }
                    lodVerts[i] = verts;
                }
                else lodVerts[i] = Array.Empty<Vec3i>();

                if (polysOffs[i] != 0)
                {
                    int a = polysOffs[i];
                    int b = NextOff(a);
                    byte[] pb = new byte[b - a];
                    Array.Copy(data, a, pb, 0, b - a);
                    lodPolys[i] = pb;
                }
                else lodPolys[i] = Array.Empty<byte>();

                if (subsOffs[i] != 0)
                {
                    int a = subsOffs[i];
                    int b = NextOff(a);
                    byte[] sb = new byte[b - a];
                    Array.Copy(data, a, sb, 0, b - a);
                    lodSubs[i] = sb;
                }
                else lodSubs[i] = Array.Empty<byte>();
            }

            return new PntFile
            {
                Path = path ?? $"{basename.ToUpperInvariant()}.PNT",
                Basename = basename.ToLowerInvariant(),
                FlagBytes = flags,
                LodVertices = lodVerts,
                LodPolyBytes = lodPolys,
                LodSubBytes = lodSubs,
            };
        }
    }

    private static int ReadU16(byte[] b, int off) => b[off] | (b[off + 1] << 8);
}

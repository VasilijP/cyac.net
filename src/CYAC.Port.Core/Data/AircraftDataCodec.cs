using System.Buffers.Binary;

namespace CYAC.Port.Core.Data;

/// <summary>
/// Turns an <see cref="AircraftDefinitionDto"/> back into the two asset bodies it was made from.
/// </summary>
/// <remarks>
/// <para>
/// This is the layout knowledge that makes <c>aircraft/&lt;name&gt;.json</c> <b>invertible</b>: the
/// document is self-describing (every tail field carries its own offset and width), so rebuilding
/// the 298-byte <c>.fmd</c> and the 504-byte <c>.fme</c> needs nothing but the document.  It lives
/// here rather than in the tool so that the runtime's loader
/// (<c>Model.Flight.AircraftDefinition.Load</c>) and <c>cyac-transform --inverse</c> cannot drift
/// apart: both go through this one implementation.
/// </para>
/// <para>
/// Format source of truth: <c>src/CYAC.Formats/EaLib/FlightModelDecoder.cs</c>, which re-emits all
/// 6 + 6 shipped assets byte-exactly.
/// </para>
/// </remarks>
public static class AircraftDataCodec
{
    /// <summary>Bytes in a <c>.fmd</c> flight-model file: 298 (<c>0x12A</c>).</summary>
    public const int FlightModelBytes = 298;

    /// <summary>Bytes in a <c>.fme</c> flight-envelope file: 504.</summary>
    public const int EnvelopeBytes = 504;

    /// <summary>Integrator blocks at the head of a <c>.fmd</c>: 9, of 16 bytes each.</summary>
    public const int InitBlockCount = 9;

    /// <summary>Stride of one integrator block.</summary>
    public const int InitBlockStride = 0x10;

    /// <summary>Where the <c>.fmd</c>'s named scalar tail starts.</summary>
    public const int TailOffset = 0x90;

    /// <summary>Curves in a <c>.fme</c>: 14, keyed by load factor -4..+9.</summary>
    public const int EnvelopeCurveCount = 14;

    /// <summary>Stride of one envelope curve record.</summary>
    public const int EnvelopeCurveStride = 0x24;

    /// <summary>Point slots in a curve, authored or filler.</summary>
    public const int EnvelopePointSlots = 8;

    /// <summary>Offset of the first point inside a curve record.</summary>
    public const int EnvelopeFirstPointOffset = 4;

    /// <summary>Rebuilds the <c>.fmd</c> body from the document.</summary>
    /// <param name="document">The aircraft document.</param>
    /// <exception cref="InvalidDataException">The document is incomplete or a field is out of range.</exception>
    public static byte[] ToFlightModelBytes(AircraftDefinitionDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        byte[] body = new byte[FlightModelBytes];

        if (document.InitBlocks is not { } blocks || blocks.Count != InitBlockCount)
        {
            throw new InvalidDataException(
                $"aircraft '{document.Name}': expected {InitBlockCount} init blocks, found " +
                $"{document.InitBlocks?.Count ?? 0}");
        }

        foreach (AircraftInitBlockDto block in blocks)
        {
            int at = block.Index * InitBlockStride;
            if (block.Index < 0 || block.Index >= InitBlockCount)
            {
                throw new InvalidDataException(
                    $"aircraft '{document.Name}': init block index {block.Index} is out of range");
            }

            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at, 4), block.Value);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at + 4, 4), block.Working);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(at + 0x08, 2), (short)block.HiBound);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(at + 0x0A, 2), (short)block.LoBound);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(at + 0x0C, 2), (short)block.BaseDir);
            BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(at + 0x0E, 2), (short)block.DirStep);
        }

        if (document.Tail is not { Count: > 0 } tail)
        {
            throw new InvalidDataException($"aircraft '{document.Name}': the flight model has no tail");
        }

        foreach (AircraftTailFieldDto field in tail)
        {
            int at = PortHex.Parse(field.Offset);
            if (at < TailOffset || at >= FlightModelBytes)
            {
                throw new InvalidDataException(
                    $"aircraft '{document.Name}': tail field '{field.Name}' sits at " +
                    $"{field.Offset}, outside 0x{TailOffset:X2}..0x{FlightModelBytes - 1:X3}");
            }

            switch (field.Type)
            {
                case "u8":
                    body[at] = (byte)field.Value;
                    break;
                case "i16":
                    BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(at, 2), (short)field.Value);
                    break;
                case "u16":
                    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(at, 2), (ushort)field.Value);
                    break;
                case "i32":
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(at, 4), field.Value);
                    break;
                default:
                    throw new InvalidDataException(
                        $"aircraft '{document.Name}': tail field '{field.Name}' has unknown type " +
                        $"'{field.Type}' (expected u8, i16, u16 or i32)");
            }
        }

        return body;
    }

    /// <summary>Rebuilds the <c>.fme</c> body from the document.</summary>
    /// <param name="document">The aircraft document.</param>
    /// <exception cref="InvalidDataException">The document is incomplete or a curve is malformed.</exception>
    public static byte[] ToEnvelopeBytes(AircraftDefinitionDto document)
    {
        ArgumentNullException.ThrowIfNull(document);
        byte[] body = new byte[EnvelopeBytes];

        if (document.Envelope is not { } curves || curves.Count != EnvelopeCurveCount)
        {
            throw new InvalidDataException(
                $"aircraft '{document.Name}': expected {EnvelopeCurveCount} envelope curves, found " +
                $"{document.Envelope?.Count ?? 0}");
        }

        for (int i = 0; i < curves.Count; i++)
        {
            EnvelopeCurveDto curve = curves[i];
            int at = i * EnvelopeCurveStride;
            body[at] = unchecked((byte)(sbyte)curve.LoadFactorG);
            body[at + 1] = (byte)curve.PointCount;
            body[at + 2] = (byte)curve.PeakIndex;
            body[at + 3] = (byte)curve.HighSpeedIndex;

            if (curve.Points is not { } points || points.Count != EnvelopePointSlots)
            {
                throw new InvalidDataException(
                    $"aircraft '{document.Name}': curve {i} must carry all {EnvelopePointSlots} " +
                    $"point slots (filler included), found {curve.Points?.Count ?? 0}");
            }

            for (int k = 0; k < points.Count; k++)
            {
                int p = at + EnvelopeFirstPointOffset + (k * 4);
                BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(p, 2), (short)points[k].AirspeedFps);
                BinaryPrimitives.WriteInt16LittleEndian(body.AsSpan(p + 2, 2), (short)points[k].AltitudeUnits);
            }
        }

        return body;
    }
}

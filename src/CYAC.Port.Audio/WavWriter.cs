namespace CYAC.Port.Audio;

/// <summary>Writes 48 kHz stereo PCM as a canonical RIFF/WAVE file.</summary>
/// <remarks>
/// Byte-for-byte the same header the emulator's <c>AudioMixer.WriteWav</c> emits, so a port WAV and
/// an emulator WAV of the same audio differ only in their samples.
/// </remarks>
public static class WavWriter
{
    /// <summary>Writes interleaved stereo signed-16 PCM.</summary>
    /// <param name="path">Where to write.</param>
    /// <param name="pcm">The samples, interleaved L,R.</param>
    public static void Write(string path, ReadOnlySpan<short> pcm)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new BinaryWriter(stream);
        int dataBytes = pcm.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)AudioFormat.Channels);
        writer.Write(AudioFormat.SampleRate);
        writer.Write(AudioFormat.SampleRate * AudioFormat.Channels * 2);
        writer.Write((short)(AudioFormat.Channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (short sample in pcm)
        {
            writer.Write(sample);
        }
    }

    /// <summary>Converts float frames to signed-16 PCM at the mixer's own scale.</summary>
    /// <param name="floats">Interleaved float samples.</param>
    /// <returns>The same samples as signed 16-bit PCM.</returns>
    public static short[] ToInt16(ReadOnlySpan<float> floats)
    {
        short[] pcm = new short[floats.Length];
        for (int i = 0; i < floats.Length; i++)
        {
            pcm[i] = (short)Math.Clamp(
                (int)(floats[i] * AudioFormat.Int16Scale), short.MinValue, short.MaxValue);
        }

        return pcm;
    }
}

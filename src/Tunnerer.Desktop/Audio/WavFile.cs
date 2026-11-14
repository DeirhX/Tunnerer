namespace Tunnerer.Desktop.Audio;

using Tunnerer.Desktop.Config;

/// <summary>Writes 16-bit PCM WAV files (sound captures and the effect dump).</summary>
public static class WavFile
{
    public static void Write(string path, ReadOnlySpan<float> samples, int channels)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        int dataBytes = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(AudioTweaks.SampleRate);
        writer.Write(AudioTweaks.SampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        foreach (float sample in samples)
            writer.Write((short)MathF.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue));
    }
}

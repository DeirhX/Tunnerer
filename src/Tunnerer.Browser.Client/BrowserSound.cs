namespace Tunnerer.Browser.Client;

using System.Runtime.InteropServices;
using Tunnerer.Desktop.Audio;
using Tunnerer.Desktop.Config;

/// <summary>A one-shot to start: a <see cref="SoundEffect"/> name, its gain, stereo position and playback rate.</summary>
public sealed record SoundPlay(string Effect, float Gain, float Pan, float Rate);

/// <summary>Where a loop should be now; the page glides it there. Gain 0 is silent.</summary>
public sealed record SoundLoop(string Effect, float Gain, float Pan, float Rate);

/// <summary>What a frame plays: the one-shots its step started and every loop's state (by handle).</summary>
public sealed record SoundFrame(SoundPlay[] Play, SoundLoop[] Loops);

/// <summary>
/// Where the browser's <see cref="SoundDirector"/> plays: it keeps what each step starts and where each loop
/// stands, and the frame after the step carries them to the page (audio.js), which plays them with the samples of
/// <see cref="BrowserSoundBank"/>.
/// </summary>
public sealed class SoundRecorder : ISoundSink
{
    private readonly List<SoundPlay> _plays = new();
    private readonly List<SoundLoop> _loops = new();

    public void Play(SoundEffect effect, float gain, float pan, float rate)
    {
        if (gain > 0f)
            _plays.Add(new SoundPlay(effect.ToString(), gain, pan, rate));
    }

    public int AddLoop(SoundEffect effect)
    {
        _loops.Add(new SoundLoop(effect.ToString(), 0f, 0f, 1f));
        return _loops.Count - 1;
    }

    public void SetLoop(int handle, float gain, float pan = 0f, float rate = 1f) =>
        _loops[handle] = _loops[handle] with
        {
            Gain = Math.Max(0f, gain),
            Pan = Math.Clamp(pan, -1f, 1f),
            Rate = Math.Clamp(rate, 0.25f, 4f),
        };

    /// <summary>
    /// What to play now. The one-shots are handed over once (<paramref name="takePlays"/>, with the frame after
    /// their step); the loops are where they stand.
    /// </summary>
    public SoundFrame Frame(bool takePlays)
    {
        SoundPlay[] plays = takePlays ? [.. _plays] : [];
        if (takePlays)
            _plays.Clear();
        return new SoundFrame(plays, [.. _loops]);
    }
}

/// <summary>One effect in the packed bank: its name, where its samples start and how many, and whether it loops.</summary>
public sealed record SoundBankEntry(string Effect, int Offset, int Length, bool Loop);

/// <summary>Where each effect lies in <see cref="BrowserSoundBank.Samples"/>.</summary>
public sealed record SoundBankLayout(int SampleRate, SoundBankEntry[] Effects);

/// <summary>
/// The desktop's synthesised <see cref="SoundBank"/>, packed for the page: every effect's mono samples one after
/// another, and where each lies. Built only when the player turns sound on.
/// </summary>
public sealed class BrowserSoundBank
{
    public BrowserSoundBank()
    {
        var bank = new SoundBank();
        var effects = Enum.GetValues<SoundEffect>();
        var entries = new SoundBankEntry[effects.Length];
        int total = 0;
        foreach (SoundEffect effect in effects)
            total += bank[effect].Length;
        Samples = new float[total];
        int offset = 0;
        for (int i = 0; i < effects.Length; i++)
        {
            float[] samples = bank[effects[i]];
            samples.CopyTo(Samples, offset);
            entries[i] = new SoundBankEntry(effects[i].ToString(), offset, samples.Length, SoundBank.IsLoop(effects[i]));
            offset += samples.Length;
        }
        Layout = new SoundBankLayout(AudioTweaks.SampleRate, entries);
    }

    public float[] Samples { get; }

    public SoundBankLayout Layout { get; }

    /// <summary>The samples as bytes (little-endian 32-bit floats), as the page takes them.</summary>
    public byte[] ToBytes() => MemoryMarshal.AsBytes(Samples.AsSpan()).ToArray();
}

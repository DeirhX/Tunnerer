namespace Emberward.Desktop.Audio;

using System.Globalization;
using System.Runtime.InteropServices;
using SDL3;
using Emberward.Desktop.Config;

/// <summary>
/// The SDL sound device. SDL calls back on its own thread whenever the device's stream wants samples and the
/// mixer supplies them, so sound keeps flowing while the game thread is busy with a slow frame.
/// </summary>
public sealed class SdlAudioOutput : IDisposable
{
    private readonly SoundMixer _mixer;

    // Held in a field so the delegate SDL calls is not collected while the stream is open.
    private readonly SDL.AudioStreamCallback _callback;
    private float[] _buffer = new float[AudioTweaks.DeviceBufferSamples * 2];
    private nint _stream;
    private bool _disposed;

    private SdlAudioOutput(SoundMixer mixer)
    {
        _mixer = mixer;
        _callback = FillStream;
    }

    /// <summary>Opens the default output device and starts it; returns null (with a log line) if there is none.</summary>
    public static SdlAudioOutput? TryOpen(SoundMixer mixer)
    {
        // Ask for small device buffers; the default can add tens of milliseconds between a shot and its sound.
        SDL.SetHint(SDL.Hints.AudioDeviceSampleFrames, AudioTweaks.DeviceBufferSamples.ToString(CultureInfo.InvariantCulture));
        if (!SDL.InitSubSystem(SDL.InitFlags.Audio))
        {
            Console.WriteLine($"[Audio] No sound: SDL audio did not start ({SDL.GetError()}).");
            return null;
        }

        var output = new SdlAudioOutput(mixer);
        // The stream takes this format and SDL converts if the device wants another rate or layout.
        var spec = new SDL.AudioSpec
        {
            Freq = AudioTweaks.SampleRate,
            Format = BitConverter.IsLittleEndian ? SDL.AudioFormat.AudioF32LE : SDL.AudioFormat.AudioF32BE,
            Channels = 2,
        };
        output._stream = SDL.OpenAudioDeviceStream(SDL.AudioDeviceDefaultPlayback, in spec, output._callback, nint.Zero);
        if (output._stream == nint.Zero)
        {
            Console.WriteLine($"[Audio] No sound: could not open an output device ({SDL.GetError()}).");
            SDL.QuitSubSystem(SDL.InitFlags.Audio);
            return null;
        }

        SDL.ResumeAudioStreamDevice(output._stream);
        return output;
    }

    /// <summary>Called by SDL on its audio thread when the stream needs <paramref name="additionalBytes"/> more.</summary>
    private void FillStream(nint userdata, nint stream, int additionalBytes, int totalBytes)
    {
        try
        {
            // Whole stereo frames only.
            int samples = additionalBytes / sizeof(float) / 2 * 2;
            if (samples <= 0)
                return;
            if (_buffer.Length < samples)
                _buffer = new float[samples];

            Span<float> mix = _buffer.AsSpan(0, samples);
            _mixer.Mix(mix);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(mix);
            SDL.PutAudioStreamData(stream, bytes, bytes.Length);
        }
        catch
        {
            // An exception must not cross into SDL's thread; a gap in the sound is the safe result.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Destroying the stream closes the device and waits for a running callback to return.
        SDL.DestroyAudioStream(_stream);
        SDL.QuitSubSystem(SDL.InitFlags.Audio);
    }
}

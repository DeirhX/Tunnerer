namespace Tunnerer.Desktop.Audio;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.SDL;
using Tunnerer.Desktop.Config;

/// <summary>
/// The SDL sound device. SDL calls back on its own thread whenever the device wants samples and the mixer fills
/// the buffer, so sound keeps flowing while the game thread is busy with a slow frame.
/// </summary>
public sealed unsafe class SdlAudioOutput : IDisposable
{
    // SDL_AUDIO_F32LSB: 32-bit float samples, little-endian.
    private const ushort FormatFloat32 = 0x8120;

    private readonly Sdl _sdl;
    private readonly uint _device;
    private GCHandle _mixerHandle;
    private bool _disposed;

    private SdlAudioOutput(Sdl sdl, uint device, GCHandle mixerHandle)
    {
        _sdl = sdl;
        _device = device;
        _mixerHandle = mixerHandle;
    }

    /// <summary>Opens the default output device and starts it; returns null (with a log line) if there is none.</summary>
    public static SdlAudioOutput? TryOpen(Sdl sdl, SoundMixer mixer)
    {
        if (sdl.InitSubSystem(Sdl.InitAudio) < 0)
        {
            Console.WriteLine($"[Audio] No sound: SDL audio did not start ({sdl.GetErrorS()}).");
            return null;
        }

        GCHandle handle = GCHandle.Alloc(mixer);
        AudioSpec wanted = default;
        wanted.Freq = AudioTweaks.SampleRate;
        wanted.Format = FormatFloat32;
        wanted.Channels = 2;
        wanted.Samples = AudioTweaks.DeviceBufferSamples;
        wanted.Callback = new PfnAudioCallback(&FillBuffer);
        wanted.Userdata = (void*)GCHandle.ToIntPtr(handle);

        // No allowed changes: SDL converts if the device wants another rate or format.
        AudioSpec obtained = default;
        uint device = sdl.OpenAudioDevice((byte*)null, 0, &wanted, &obtained, 0);
        if (device == 0)
        {
            Console.WriteLine($"[Audio] No sound: could not open an output device ({sdl.GetErrorS()}).");
            handle.Free();
            sdl.QuitSubSystem(Sdl.InitAudio);
            return null;
        }

        sdl.PauseAudioDevice(device, 0);
        return new SdlAudioOutput(sdl, device, handle);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FillBuffer(void* userdata, byte* stream, int byteCount)
    {
        var output = new Span<float>(stream, byteCount / sizeof(float));
        try
        {
            if (GCHandle.FromIntPtr((nint)userdata).Target is SoundMixer mixer)
                mixer.Mix(output);
            else
                output.Clear();
        }
        catch
        {
            // An exception must not cross into SDL's thread; a silent buffer is the safe result.
            output.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Closing waits for a running callback to return, so the handle can be freed afterwards.
        _sdl.CloseAudioDevice(_device);
        _mixerHandle.Free();
        _sdl.QuitSubSystem(Sdl.InitAudio);
    }
}

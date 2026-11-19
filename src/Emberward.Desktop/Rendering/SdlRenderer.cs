namespace Emberward.Desktop.Rendering;

using SDL3;
using Emberward.Core.Types;
using Emberward.Desktop.Config;

/// <summary>
/// The SDL3 window, event pump and mouse state. Render backends draw into <see cref="NativeWindow"/>.
/// </summary>
public sealed class SdlRenderer : IDisposable
{
    private nint _window;
    private bool _disposed;

    public nint NativeWindow => _window;

    public SdlRenderer(string title, Size windowSize)
    {
        if (!SDL.Init(SDL.InitFlags.Video | SDL.InitFlags.Events))
            throw new InvalidOperationException($"SDL_Init failed: {SDL.GetError()}");

        // Fullscreen-desktop borderless by default; a plain resizable window when --windowed is requested, and a
        // hidden borderless window at full display resolution when --hidden is requested (automated runs and
        // performance measurement that must not show anything on screen).
        var windowed = DesktopDisplayOptions.WindowedSize;
        uint displayId = ChooseDisplay(out Size largestDisplay);
        SDL.WindowFlags windowFlags;
        if (DesktopDisplayOptions.Hidden)
        {
            windowFlags = SDL.WindowFlags.Hidden | SDL.WindowFlags.Borderless;
            windowSize = windowed ?? largestDisplay;
            Console.WriteLine($"[Display] Hidden window {windowSize.X}x{windowSize.Y} (nothing is shown on screen).");
        }
        else if (windowed is Size requestedSize)
        {
            windowFlags = SDL.WindowFlags.Resizable;
            windowSize = requestedSize;
            Console.WriteLine($"[Display] Windowed mode {windowSize.X}x{windowSize.Y}.");
        }
        else
        {
            // SDL3's fullscreen flag uses the desktop mode unless a display mode is set explicitly.
            windowFlags = SDL.WindowFlags.Borderless | SDL.WindowFlags.Fullscreen;
        }

        // Ask for a surface in device pixels. Without this a Retina display gets a point-sized surface that the OS
        // stretches 2x, which is where the soft, upscaled look came from.
        windowFlags |= SDL.WindowFlags.HighPixelDensity;

        _window = SDL.CreateWindow(title, windowSize.X, windowSize.Y, windowFlags);
        if (_window == nint.Zero)
            throw new InvalidOperationException($"SDL_CreateWindow failed: {SDL.GetError()}");

        int centered = (int)(SDL.WindowPosCenteredMask | displayId);
        SDL.SetWindowPosition(_window, centered, centered);

        // SDL2 delivered text input events by default; SDL3 needs them switched on per window (ImGui text fields).
        SDL.StartTextInput(_window);
    }

    /// <summary>
    /// The requested display (<see cref="DesktopDisplayOptions.DisplayIndex"/>), or the primary display when none is
    /// requested or the index does not exist. Also reports the largest display's size for hidden runs.
    /// </summary>
    private static uint ChooseDisplay(out Size largestDisplay)
    {
        uint[] displays = SDL.GetDisplays(out int displayCount) ?? [];
        largestDisplay = DesktopScreenTweaks.WindowSize;
        long largestArea = 0;
        for (int i = 0; i < displayCount; i++)
        {
            if (!SDL.GetDisplayBounds(displays[i], out SDL.Rect bounds))
                continue;
            Console.WriteLine($"[Display] {i}: {bounds.W}x{bounds.H} at ({bounds.X},{bounds.Y})");
            long area = (long)bounds.W * bounds.H;
            if (area > largestArea)
            {
                largestArea = area;
                largestDisplay = new Size(bounds.W, bounds.H);
            }
        }

        int? requested = DesktopDisplayOptions.DisplayIndex;
        if (requested is int index && index < displayCount)
        {
            Console.WriteLine($"[Display] Opening the window on display {index}.");
            return displays[index];
        }

        if (requested is int missing)
            Console.WriteLine($"[Display] Display {missing} not found ({displayCount} available); using the primary display.");
        return SDL.GetPrimaryDisplay();
    }

    /// <summary>Window size in points: the unit SDL reports mouse positions in, and the unit the UI is laid out in.</summary>
    public (int w, int h) GetWindowSize()
    {
        SDL.GetWindowSize(_window, out int w, out int h);
        return (w, h);
    }

    /// <summary>Window size in device pixels: the size of the surface the frame is drawn into.</summary>
    public (int w, int h) GetWindowSizeInPixels()
    {
        SDL.GetWindowSizeInPixels(_window, out int w, out int h);
        return (w, h);
    }

    /// <summary>Device pixels per point: 2 on a Retina display, 1 on a standard one.</summary>
    public float PixelDensity
    {
        get
        {
            float density = SDL.GetWindowPixelDensity(_window);
            return density > 0f ? density : 1f;
        }
    }

    public (int x, int y, uint buttons) GetMouseState()
    {
        SDL.MouseButtonFlags buttons = SDL.GetMouseState(out float mx, out float my);
        return ((int)mx, (int)my, (uint)buttons);
    }

    /// <summary>
    /// Polls SDL events. Returns false if the window was closed. Escape is left to the event handler, which decides
    /// whether it pauses or quits.
    /// Calls <paramref name="eventHandler"/> for each event before checking quit.
    /// </summary>
    public bool PollEvents(Action<SDL.Event>? eventHandler = null)
    {
        while (SDL.PollEvent(out SDL.Event ev))
        {
            eventHandler?.Invoke(ev);

            if (ev.Type == (uint)SDL.EventType.Quit)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_window != nint.Zero) SDL.DestroyWindow(_window);
        _window = nint.Zero;
        SDL.Quit();
    }
}

namespace Tunnerer.Desktop.Rendering;

using Silk.NET.SDL;
using Tunnerer.Core.Types;
using System.Runtime.InteropServices;
using Tunnerer.Desktop.Config;

/// <summary>
/// SDL2 native-window wrapper used by the DX backends.
/// </summary>

public sealed unsafe class SdlRenderer : IDisposable
{
    private readonly Sdl _sdl;
    private Window* _window;
    private bool _disposed;

    public Sdl Sdl => _sdl;
    public Window* NativeWindow => _window;

    public SdlRenderer(string title, Size windowSize)
    {
        _sdl = Sdl.GetApi();

        if (_sdl.Init(Sdl.InitVideo | Sdl.InitEvents) < 0)
            throw new Exception($"SDL_Init failed: {Marshal.PtrToStringAnsi((nint)_sdl.GetError())}");

        // Fullscreen-desktop borderless by default; a plain resizable window when --windowed is requested, and a
        // hidden borderless window at full display resolution when --hidden is requested (automated runs and
        // performance measurement that must not show anything on screen).
        var windowed = DesktopDisplayOptions.WindowedSize;
        int windowPos = ChooseWindowPosition(out Size largestDisplay);
        uint windowFlags;
        if (DesktopDisplayOptions.Hidden)
        {
            windowFlags = (uint)(WindowFlags.Hidden | WindowFlags.Borderless);
            windowSize = windowed ?? largestDisplay;
            Console.WriteLine($"[Display] Hidden window {windowSize.X}x{windowSize.Y} (nothing is shown on screen).");
        }
        else if (windowed is Size requestedSize)
        {
            windowFlags = (uint)(WindowFlags.Shown | WindowFlags.Resizable);
            windowSize = requestedSize;
            Console.WriteLine($"[Display] Windowed mode {windowSize.X}x{windowSize.Y}.");
        }
        else
        {
            windowFlags = (uint)(WindowFlags.Shown | WindowFlags.Borderless | WindowFlags.FullscreenDesktop);
        }

        _window = _sdl.CreateWindow(
            title,
            windowPos, windowPos,
            windowSize.X, windowSize.Y,
            windowFlags);

        if (_window == null)
            throw new Exception($"SDL_CreateWindow failed: {Marshal.PtrToStringAnsi((nint)_sdl.GetError())}");

    }

    /// <summary>
    /// Centered on the requested display (<see cref="DesktopDisplayOptions.DisplayIndex"/>), or on the primary
    /// display when none is requested or the index does not exist.
    /// </summary>
    private int ChooseWindowPosition(out Size largestDisplay)
    {
        int displayCount = _sdl.GetNumVideoDisplays();
        largestDisplay = Tunnerer.Desktop.Config.DesktopScreenTweaks.WindowSize;
        long largestArea = 0;
        for (int i = 0; i < displayCount; i++)
        {
            Silk.NET.Maths.Rectangle<int> bounds = default;
            if (_sdl.GetDisplayBounds(i, &bounds) != 0)
                continue;
            Console.WriteLine($"[Display] {i}: {bounds.Size.X}x{bounds.Size.Y} at ({bounds.Origin.X},{bounds.Origin.Y})");
            long area = (long)bounds.Size.X * bounds.Size.Y;
            if (area > largestArea)
            {
                largestArea = area;
                largestDisplay = new Size(bounds.Size.X, bounds.Size.Y);
            }
        }

        int? requested = DesktopDisplayOptions.DisplayIndex;
        if (requested is int index && index < displayCount)
        {
            Console.WriteLine($"[Display] Opening the window on display {index}.");
            return Sdl.WindowposCentered | index;
        }

        if (requested is int missing)
            Console.WriteLine($"[Display] Display {missing} not found ({displayCount} available); using the primary display.");
        return Sdl.WindowposCentered;
    }

    public (int w, int h) GetWindowSize()
    {
        int w, h;
        _sdl.GetWindowSize(_window, &w, &h);
        return (w, h);
    }

    public (int x, int y, uint buttons) GetMouseState()
    {
        int mx, my;
        uint buttons = _sdl.GetMouseState(&mx, &my);
        return (mx, my, buttons);
    }

    public void SwapWindow()
    {
        // DX backends present via swap chain; SDL swap is unused.
    }

    /// <summary>
    /// Polls SDL events. Returns false if the window was closed. Escape is left to the event handler, which decides
    /// whether it pauses or quits.
    /// Calls <paramref name="eventHandler"/> for each event before checking quit.
    /// </summary>
    public bool PollEvents(Action<Event>? eventHandler = null)
    {
        Event ev;
        while (_sdl.PollEvent(&ev) != 0)
        {
            eventHandler?.Invoke(ev);

            if (ev.Type == (uint)EventType.Quit)
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_window != null) _sdl.DestroyWindow(_window);
        _sdl.Quit();
        _sdl.Dispose();
    }
}

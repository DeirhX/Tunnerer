namespace Tunnerer.Desktop.Rendering;

/// <summary>Where screenshots go and how they are written, for every backend.</summary>
public static class ScreenshotFiles
{
    /// <summary>
    /// A new screenshot path: <c>TUNNERER_SCREENSHOT_DIR</c> if set, otherwise <c>screenshots/</c> next to the
    /// executable, with a UTC timestamp and the label in the file name.
    /// </summary>
    public static string NewPath(string? label)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
        string cleanLabel = string.IsNullOrWhiteSpace(label) ? "frame" : SanitizeFilePart(label);
        string dir = Environment.GetEnvironmentVariable("TUNNERER_SCREENSHOT_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{stamp}_{cleanLabel}.ppm");
    }

    /// <summary>Writes BGRA8 rows (<paramref name="rowPitch"/> bytes apart) as a binary PPM.</summary>
    public static unsafe void WriteBgraAsPpm(string outPath, byte* bgra, int width, int height, int rowPitch)
    {
        string? parent = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        fs.Write(header, 0, header.Length);

        byte[] rgb = new byte[width * 3];
        for (int y = 0; y < height; y++)
        {
            byte* row = bgra + (long)y * rowPitch;
            int dst = 0;
            for (int x = 0; x < width; x++)
            {
                int si = x * 4;
                rgb[dst++] = row[si + 2];
                rgb[dst++] = row[si + 1];
                rgb[dst++] = row[si + 0];
            }
            fs.Write(rgb, 0, rgb.Length);
        }
    }

    private static string SanitizeFilePart(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            buffer[i] = Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '_' : c;
        }
        return new string(buffer);
    }
}

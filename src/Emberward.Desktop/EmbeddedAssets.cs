namespace Emberward.Desktop;

using System.Reflection;

/// <summary>
/// The game's files that ship inside the executable: the shaders, the HUD art, the logo and the UI font. The project
/// embeds each one under its path with forward slashes (for example <c>resources/hud/heat_icon.png</c>, or
/// <c>Shaders/BlitPs.json</c>), so the published game is one file with nothing beside it.
/// </summary>
public static class EmbeddedAssets
{
    private static readonly Assembly Owner = typeof(EmbeddedAssets).Assembly;

    /// <summary>True when the executable carries an asset with this name.</summary>
    public static bool Exists(string name) => Owner.GetManifestResourceInfo(name) is not null;

    /// <summary>The bytes of an asset. Throws when the executable does not carry it, which means the build left it out.</summary>
    public static byte[] ReadBytes(string name)
    {
        using Stream stream = Owner.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"The executable has no asset named {name}. Was it embedded by the build?", name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The text of an asset, read as UTF-8.</summary>
    public static string ReadText(string name) => System.Text.Encoding.UTF8.GetString(ReadBytes(name));
}

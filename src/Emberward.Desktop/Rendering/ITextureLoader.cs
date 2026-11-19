namespace Emberward.Desktop.Rendering;

public interface ITextureLoader : IDisposable
{
    /// <summary>Decodes an image the executable embeds (an <see cref="EmbeddedAssets"/> name) into a texture.</summary>
    nint LoadTexture(string assetName);
}

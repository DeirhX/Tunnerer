namespace Emberward.Desktop.Rendering;

public interface ITextureLoader : IDisposable
{
    nint LoadTexture(string path);
}

namespace Emberward.Desktop;

using System.Numerics;
using Emberward.Core.Types;
using ImGuiNET;

public partial class Game
{
    // Present before generating the world so the window has a useful first frame during startup.
    private void DrawStartupSplash()
    {
        const string logoAsset = "resources/emberward-logo.png";
        // The splash is decoration: a missing logo must not stop the game from starting.
        if (!EmbeddedAssets.Exists(logoAsset))
            return;
        nint logo = _textures.LoadTexture(logoAsset);
        var (width, height) = _renderer.GetWindowSize();
        if (width <= 0 || height <= 0)
            return;

        _renderBackend.ClearFrame(new Size(width, height), new Color(8, 15, 19, 255));
        _renderBackend.NewFrame(width, height, 1f / 60f);
        // The logo is 2081 x 400 (tools/brand/make_logo.py), drawn no larger than the PNG's 2400 pixels.
        float logoWidth = Math.Min(Math.Min(width * 0.62f, height * 2.6f), 2400f);
        var logoSize = new Vector2(logoWidth, logoWidth * 400f / 2081f);
        var topLeft = new Vector2((width - logoSize.X) / 2f, (height - logoSize.Y) / 2f - height * 0.04f);
        var draw = ImGui.GetBackgroundDrawList();
        draw.AddImage(logo, topLeft, topLeft + logoSize);
        const string label = "LOADING";
        var textSize = ImGui.CalcTextSize(label);
        draw.AddText(new Vector2((width - textSize.X) / 2f, topLeft.Y + logoSize.Y + 10f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.72f, 0.72f, 0.68f, 1f)), label);
        _renderBackend.Render();
    }
}

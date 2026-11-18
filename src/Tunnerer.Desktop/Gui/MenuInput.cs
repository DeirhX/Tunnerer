namespace Tunnerer.Desktop.Gui;

using System.Numerics;
using ImGuiNET;

/// <summary>The mouse rules the menus share.</summary>
public static class MenuInput
{
    public static bool Contains(Vector2 min, Vector2 size, Vector2 point) =>
        point.X >= min.X && point.Y >= min.Y && point.X < min.X + size.X && point.Y < min.Y + size.Y;

    /// <summary>
    /// A click on a button: the left button was released over it, and it was pressed there too, while the screen was
    /// open (<paramref name="armed"/>). Left click is also "fire", so without the rule, pausing while shooting and
    /// letting go over a button would press it.
    /// </summary>
    public static bool Clicked(Vector2 min, Vector2 size, bool hovered, bool armed) =>
        hovered && armed && ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
        Contains(min, size, ImGui.GetIO().MouseClickedPos[0]);

    /// <summary>The mouse is over the rectangle.</summary>
    public static bool Hovered(Vector2 min, Vector2 size) => Contains(min, size, ImGui.GetIO().MousePos);
}

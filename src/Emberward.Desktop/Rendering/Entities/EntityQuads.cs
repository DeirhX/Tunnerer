namespace Emberward.Desktop.Rendering.Entities;

using Emberward.Core.Types;

/// <summary>
/// The entity layer's vertex list for one displayed frame, and the frame values every family emitter needs: the
/// view, the shaders' animation phase and how far the display is between the last two simulation steps.
/// </summary>
public sealed class EntityQuads
{
    private const int VerticesPerQuad = 6;

    private EntityVertex[] _vertices = new EntityVertex[1024];
    private int _vertexCount;

    public EntityVertex[] Vertices => _vertices;
    public int VertexCount => _vertexCount;

    public RenderView View { get; private set; }

    /// <summary>The shaders' animation phase (seconds, wrapped).</summary>
    public float Phase { get; private set; }

    /// <summary>0 at the previous simulation step, 1 at the latest.</summary>
    public float Alpha { get; private set; }

    public void Begin(in RenderView view, float phase, float alpha)
    {
        _vertexCount = 0;
        View = view;
        Phase = phase;
        Alpha = alpha;
    }

    /// <summary>Screen pixel of the centre of world cell coordinate <paramref name="cellX"/>, as drawn now.</summary>
    public float ScreenX(float cellX) => (cellX + 0.5f) * View.PixelScale - View.CameraPixels.X;

    /// <summary>Screen pixel of the centre of world cell coordinate <paramref name="cellY"/>, as drawn now.</summary>
    public float ScreenY(float cellY) => (cellY + 0.5f) * View.PixelScale - View.CameraPixels.Y;

    /// <summary>The world coordinate of the centre of cell <paramref name="cell"/>: what a quad's anchor is given in.</summary>
    public static float Centre(float cell) => cell + 0.5f;

    public bool IsOffScreen(float x, float y, float margin) =>
        x < -margin || y < -margin || x > View.ViewSize.X + margin || y > View.ViewSize.Y + margin;

    /// <summary>
    /// Adds a quad for a shape whose colours the entity shader owns (machines, beams, the sense ring): its vertex
    /// colour is black and unused. Only tanks, shots, the reach area and the coverage field take a colour from here.
    /// </summary>
    public void Add(
        float cx, float cy, float axisX, float axisY,
        float minX, float maxX, float minY, float maxY,
        float kind, float p0, float p1, float p2, float p3, PositionF motion = default) =>
        Add(cx, cy, axisX, axisY, minX, maxX, minY, maxY, default, kind, p0, p1, p2, p3, motion);

    /// <summary>
    /// Adds a quad anchored at world coordinate (<paramref name="cx"/>, <paramref name="cy"/>) (cells; a cell's centre
    /// is <see cref="Centre"/>), whose local +x axis points along (<paramref name="axisX"/>, <paramref name="axisY"/>)
    /// and which spans the given local rectangle in cells. The shader draws it at the anchor plus
    /// <paramref name="motion"/> (cells per simulation step) times the step's progress.
    /// </summary>
    public void Add(
        float cx, float cy, float axisX, float axisY,
        float minX, float maxX, float minY, float maxY,
        Color color, float kind, float p0, float p1, float p2, float p3, PositionF motion = default)
    {
        if (_vertexCount + VerticesPerQuad > _vertices.Length)
            Array.Resize(ref _vertices, _vertices.Length * 2);

        EntityVertex Corner(float lx, float ly) => new()
        {
            CellX = cx + (lx * axisX - ly * axisY),
            CellY = cy + (lx * axisY + ly * axisX),
            LocalX = lx,
            LocalY = ly,
            R = color.R / 255f,
            G = color.G / 255f,
            B = color.B / 255f,
            Kind = kind,
            P0 = p0,
            P1 = p1,
            P2 = p2,
            P3 = p3,
            MotionX = motion.X,
            MotionY = motion.Y,
        };

        EntityVertex a = Corner(minX, minY), b = Corner(maxX, minY), c = Corner(maxX, maxY), d = Corner(minX, maxY);
        _vertices[_vertexCount++] = a;
        _vertices[_vertexCount++] = b;
        _vertices[_vertexCount++] = c;
        _vertices[_vertexCount++] = a;
        _vertices[_vertexCount++] = c;
        _vertices[_vertexCount++] = d;
    }
}

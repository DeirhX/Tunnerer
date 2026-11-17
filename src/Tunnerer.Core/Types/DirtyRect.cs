namespace Tunnerer.Core.Types;

/// <summary>
/// The bounding rectangle of the cells marked since it was last cleared: what changed, for the renderer to upload.
/// A mutable struct: keep it in a field and call its members on the field, never on a copy.
/// </summary>
public struct DirtyRect
{
    private bool _any;
    private int _minX, _minY, _maxX, _maxY;

    /// <summary>Grows the rectangle to include the cell.</summary>
    public void Include(int x, int y)
    {
        if (!_any)
        {
            _any = true;
            _minX = _maxX = x;
            _minY = _maxY = y;
            return;
        }
        if (x < _minX) _minX = x;
        if (x > _maxX) _maxX = x;
        if (y < _minY) _minY = y;
        if (y > _maxY) _maxY = y;
    }

    public readonly bool TryGet(out Rect rect)
    {
        rect = _any ? RectMath.FromMinMaxInclusive(_minX, _minY, _maxX, _maxY) : default;
        return _any;
    }

    public void Clear() => _any = false;
}

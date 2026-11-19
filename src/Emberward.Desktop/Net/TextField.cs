namespace Emberward.Desktop.Net;

/// <summary>
/// One line of text being edited: the text and a caret. It knows nothing of keys or drawing, so the network menu's
/// fields are tested without a window. Text that does not fit (a control character, a character the field does not
/// take, or more than <see cref="MaxLength"/>) is dropped as it is typed or pasted.
/// </summary>
public sealed class TextField
{
    private string _text = string.Empty;

    public TextField(int maxLength, bool masked = false, Func<char, bool>? accepts = null)
    {
        MaxLength = maxLength;
        Masked = masked;
        _accepts = accepts;
    }

    private readonly Func<char, bool>? _accepts;

    public int MaxLength { get; }

    /// <summary>Shown as dots, not letters (a secret).</summary>
    public bool Masked { get; }

    public string Text => _text;

    /// <summary>Where typing goes: 0 is before the first character.</summary>
    public int Caret { get; private set; }

    /// <summary>Replaces the text and puts the caret at its end.</summary>
    public void Set(string text)
    {
        _text = string.Empty;
        Caret = 0;
        Insert(text);
    }

    /// <summary>Types <paramref name="text"/> at the caret, as much of it as fits.</summary>
    public void Insert(string text)
    {
        foreach (char c in text)
        {
            if (_text.Length >= MaxLength)
                break;
            if (char.IsControl(c) || char.IsSurrogate(c) || _accepts?.Invoke(c) == false)
                continue;
            _text = _text.Insert(Caret, c.ToString());
            Caret++;
        }
    }

    public void Backspace()
    {
        if (Caret == 0)
            return;
        _text = _text.Remove(Caret - 1, 1);
        Caret--;
    }

    public void Delete()
    {
        if (Caret < _text.Length)
            _text = _text.Remove(Caret, 1);
    }

    public void MoveLeft() => Caret = Math.Max(0, Caret - 1);

    public void MoveRight() => Caret = Math.Min(_text.Length, Caret + 1);

    public void Home() => Caret = 0;

    public void End() => Caret = _text.Length;
}

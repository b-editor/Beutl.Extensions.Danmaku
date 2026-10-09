using System.ComponentModel;
using System.Runtime.CompilerServices;
using Beutl.Extensions.Danmaku.Comments;

namespace Beutl.Extensions.Danmaku.Editing;

/// <summary>One editable row of the comment table. Edits are written back through the owning tab.</summary>
public sealed class CommentRowViewModel : INotifyPropertyChanged
{
    private readonly DanmakuTabViewModel _owner;
    private string _timeText = "";
    private int _positionIndex;
    private int _sizeIndex;
    private string _colorText = "";
    private string _text = "";
    private bool _loading;

    internal CommentRowViewModel(DanmakuTabViewModel owner, int index, DanmakuComment comment)
    {
        _owner = owner;
        Load(index, comment);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The index of the comment in the drawable's list.</summary>
    public int Index { get; private set; }

    /// <summary>The 1-based row number shown in the table.</summary>
    public int Number => Index + 1;

    public DanmakuComment Comment { get; private set; } = null!;

    public string TimeText
    {
        get => _timeText;
        set => Set(ref _timeText, value);
    }

    /// <summary>The index into <see cref="DanmakuTabViewModel.PositionNames"/>.</summary>
    public int PositionIndex
    {
        get => _positionIndex;
        set => Set(ref _positionIndex, value);
    }

    /// <summary>The index into <see cref="DanmakuTabViewModel.SizeNames"/>.</summary>
    public int SizeIndex
    {
        get => _sizeIndex;
        set => Set(ref _sizeIndex, value);
    }

    /// <summary>"#RRGGBB", a colour name such as "red", or empty for the default white.</summary>
    public string ColorText
    {
        get => _colorText;
        set => Set(ref _colorText, value);
    }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    // Read-only views of the stored comment for the list.

    public string PositionName => DanmakuTabViewModel.PositionNames[(int)Comment.Position];

    public string SizeName => DanmakuTabViewModel.SizeNames[(int)Comment.Size];

    public Avalonia.Media.Color SwatchColor
    {
        get
        {
            Beutl.Media.Color c = Comment.FillColor;
            return Avalonia.Media.Color.FromArgb(c.A, c.R, c.G, c.B);
        }
    }

    /// <summary>The text on one line, with line breaks shown as "↵".</summary>
    public string SingleLineText => Comment.Text.Replace("\n", " ↵ ");

    internal void Load(int index, DanmakuComment comment)
    {
        _loading = true;
        try
        {
            if (Index != index)
            {
                Index = index;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Number)));
            }

            Comment = comment;
            TimeText = CommentWriter.FormatTime(comment.Time);
            PositionIndex = (int)comment.Position;
            SizeIndex = (int)comment.Size;
            ColorText = comment.Color is { } color ? CommentCommands.FormatColor(color) : "";
            Text = comment.Text;
        }
        finally
        {
            _loading = false;
        }

        foreach (string name in (string[])[nameof(PositionName), nameof(SizeName), nameof(SwatchColor), nameof(SingleLineText)])
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Builds the comment the row describes, or returns null when a field cannot be parsed.</summary>
    internal DanmakuComment? TryBuild()
    {
        if (!CommentParser.TryParseTime(TimeText.Trim(), out TimeSpan time))
            return null;

        Beutl.Media.Color? color = null;
        if (!string.IsNullOrWhiteSpace(ColorText))
        {
            if (!CommentCommands.TryParseColor(ColorText, out Beutl.Media.Color parsed))
                return null;
            color = parsed;
        }

        if (string.IsNullOrWhiteSpace(Text))
            return null;

        return new DanmakuComment(time, Text, (CommentPosition)Math.Clamp(PositionIndex, 0, 2),
            (CommentSize)Math.Clamp(SizeIndex, 0, 2), color);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (!_loading)
            _owner.OnRowEdited(this);
    }
}

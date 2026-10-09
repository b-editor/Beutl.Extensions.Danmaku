using Beutl.Media;

namespace Beutl.Extensions.Danmaku.Comments;

public enum CommentPosition
{
    /// <summary>Scrolls from right to left ("naka").</summary>
    Scroll,

    /// <summary>Fixed at the top ("ue").</summary>
    Top,

    /// <summary>Fixed at the bottom ("shita").</summary>
    Bottom,
}

public enum CommentSize
{
    Medium,
    Big,
    Small,
}

/// <summary>One comment: when it is posted, what it says, and how it is styled.</summary>
public sealed record DanmakuComment(
    TimeSpan Time,
    string Text,
    CommentPosition Position = CommentPosition.Scroll,
    CommentSize Size = CommentSize.Medium,
    Color? Color = null)
{
    public Color FillColor => Color ?? Colors.White;

    public string[] Lines => Text.Split('\n');
}

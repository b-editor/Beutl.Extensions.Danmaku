using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Layout;
using Beutl.Media;
using Beutl.Media.TextFormatting;

namespace Beutl.Extensions.Danmaku.Graphics;

/// <summary>The arranged comments for one screen size, with the shaped text of every placed line.</summary>
/// <remarks>
/// Drawn texts are never disposed here: a render node recorded for an earlier frame may still hold them, and
/// their Skia handles are finalizable, so a replaced layout is simply left to the garbage collector.
/// </remarks>
internal sealed class DanmakuFrameLayout
{
    private readonly IReadOnlyList<DanmakuComment> _comments;
    private readonly FontFamily _font;
    private readonly FormattedText[]?[] _lines;
    private readonly List<FormattedText> _measuring = [];

    public DanmakuFrameLayout(IReadOnlyList<DanmakuComment> comments, DanmakuLayoutSettings settings, FontFamily font)
    {
        _comments = comments;
        _font = font;
        Settings = settings;
        Placed = DanmakuLayout.Arrange(comments, settings, MeasureWidth);
        _lines = new FormattedText[Placed.Length][];
        LongestDuration = Placed.Length == 0 ? TimeSpan.Zero : Placed.Max(p => p.End - p.Start);

        foreach (FormattedText text in _measuring)
            text.Dispose();
        _measuring.Clear();
    }

    public DanmakuLayoutSettings Settings { get; }

    public PlacedComment[] Placed { get; }

    public TimeSpan LongestDuration { get; }

    public bool Matches(IReadOnlyList<DanmakuComment> comments, DanmakuLayoutSettings settings, FontFamily font)
    {
        return ReferenceEquals(comments, _comments) && settings == Settings && font.Equals(_font);
    }

    public int FindFirstStartingAfter(TimeSpan time)
    {
        int low = 0;
        int high = Placed.Length;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (Placed[mid].Start < time)
                low = mid + 1;
            else
                high = mid;
        }

        return low;
    }

    /// <summary>The shaped lines of the placement at <paramref name="placedIndex"/>, created on first use.</summary>
    public FormattedText[] GetLines(int placedIndex)
    {
        if (_lines[placedIndex] is { } lines)
            return lines;

        PlacedComment item = Placed[placedIndex];
        string[] texts = _comments[item.Index].Lines;
        lines = new FormattedText[texts.Length];
        for (int i = 0; i < texts.Length; i++)
            lines[i] = CreateText(texts[i], item.FontSize);
        _lines[placedIndex] = lines;
        return lines;
    }

    private float MeasureWidth(DanmakuComment comment, float fontSize)
    {
        float width = 0;
        foreach (string line in comment.Lines)
        {
            FormattedText text = CreateText(line, fontSize);
            _measuring.Add(text);
            width = Math.Max(width, text.Bounds.Width);
        }

        return width;
    }

    private FormattedText CreateText(string text, float fontSize)
    {
        return new FormattedText
        {
            Font = _font,
            Size = Math.Max(1, fontSize),
            Text = new StringSpan(text, 0, text.Length),
        };
    }
}

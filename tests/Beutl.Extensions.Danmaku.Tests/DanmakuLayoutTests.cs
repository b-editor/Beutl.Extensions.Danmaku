using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Layout;

namespace Beutl.Extensions.Danmaku.Tests;

public class DanmakuLayoutTests
{
    private static readonly DanmakuLayoutSettings s_settings = new(
        Width: 1000,
        Height: 384,
        ScrollDuration: TimeSpan.FromSeconds(4),
        FixedDuration: TimeSpan.FromSeconds(3));

    // Every character is as wide as the font size.
    private static float Measure(DanmakuComment comment, float fontSize) =>
        comment.Lines.Max(l => l.Length) * fontSize;

    private static DanmakuComment Scroll(double seconds, string text = "コメント") =>
        new(TimeSpan.FromSeconds(seconds), text);

    [Test]
    public void FontSizesFollowThe384PixelReference()
    {
        Assert.That(DanmakuLayout.GetFontSize(CommentSize.Medium, s_settings), Is.EqualTo(24));
        Assert.That(DanmakuLayout.GetFontSize(CommentSize.Big, s_settings with { Height = 768 }), Is.EqualTo(78));
        Assert.That(DanmakuLayout.GetFontSize(CommentSize.Small, s_settings with { FontScale = 2 }), Is.EqualTo(30));
    }

    [Test]
    public void ScrollingCommentStartsOneQuarterEarlyAndCrossesTheScreen()
    {
        PlacedComment placed = DanmakuLayout.Arrange([Scroll(10)], s_settings, Measure).Single();

        Assert.That(placed.Start, Is.EqualTo(TimeSpan.FromSeconds(9)));
        Assert.That(placed.End, Is.EqualTo(TimeSpan.FromSeconds(13)));
        Assert.That(placed.GetX(placed.Start, 1000), Is.EqualTo(1000));
        Assert.That(placed.GetX(placed.End, 1000), Is.EqualTo(-placed.Width).Within(0.001));
    }

    [Test]
    public void SimultaneousCommentsStackInRows()
    {
        PlacedComment[] placed = DanmakuLayout.Arrange([Scroll(1), Scroll(1), Scroll(1)], s_settings, Measure);

        float rowHeight = 24 * 1.2f;
        Assert.That(placed.Select(p => p.Y), Is.EqualTo(new[] { 0f, rowHeight, rowHeight * 2 }).Within(0.001));
        Assert.That(placed.Any(p => p.IsOverflow), Is.False);
    }

    [Test]
    public void LaterCommentReusesRowOnceTheEarlierOneHasMovedOn()
    {
        // The first comment is 96 px wide and fully on screen after 4 s * 96 / 1096.
        PlacedComment[] placed = DanmakuLayout.Arrange([Scroll(1), Scroll(1.5)], s_settings, Measure);

        Assert.That(placed[1].Y, Is.EqualTo(0));
    }

    [Test]
    public void FasterLongCommentDoesNotShareRowWhenItWouldCatchUp()
    {
        // A much wider comment moves faster and would overtake the short one before it leaves.
        DanmakuComment shortComment = Scroll(1, "短い");
        DanmakuComment longComment = Scroll(1.3, new string('長', 30));

        PlacedComment[] placed = DanmakuLayout.Arrange([shortComment, longComment], s_settings, Measure);

        Assert.That(placed[0].Y, Is.EqualTo(0));
        Assert.That(placed[1].Y, Is.GreaterThan(0));
    }

    [Test]
    public void TopAndBottomCommentsStackFromTheirEdges()
    {
        DanmakuComment[] comments =
        [
            new(TimeSpan.FromSeconds(1), "上1", CommentPosition.Top),
            new(TimeSpan.FromSeconds(1), "上2", CommentPosition.Top),
            new(TimeSpan.FromSeconds(1), "下1", CommentPosition.Bottom),
            new(TimeSpan.FromSeconds(1), "下2", CommentPosition.Bottom),
        ];

        PlacedComment[] placed = DanmakuLayout.Arrange(comments, s_settings, Measure);
        float rowHeight = 24 * 1.2f;

        Assert.That(placed.Select(p => p.Y), Is.EqualTo(new[] { 0f, rowHeight, 384 - rowHeight, 384 - rowHeight * 2 }).Within(0.001));
        Assert.That(placed[0].GetX(TimeSpan.FromSeconds(2), 1000), Is.EqualTo((1000 - placed[0].Width) / 2));
        Assert.That(placed[0].End - placed[0].Start, Is.EqualTo(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public void FixedCommentWiderThanScreenIsShrunk()
    {
        var comment = new DanmakuComment(TimeSpan.Zero, new string('あ', 100), CommentPosition.Top);
        PlacedComment placed = DanmakuLayout.Arrange([comment], s_settings, Measure).Single();

        Assert.That(placed.Width, Is.EqualTo(1000).Within(0.01));
        Assert.That(placed.FontSize, Is.EqualTo(10).Within(0.01));
    }

    [Test]
    public void OverflowingCommentsArePlacedOrDroppedBySetting()
    {
        // 384 / 28.8 leaves room for 13 rows.
        DanmakuComment[] comments = Enumerable.Range(0, 20).Select(_ => Scroll(1)).ToArray();

        PlacedComment[] shown = DanmakuLayout.Arrange(comments, s_settings, Measure);
        PlacedComment[] dropped = DanmakuLayout.Arrange(comments, s_settings with { ShowOverflow = false }, Measure);

        Assert.That(shown, Has.Length.EqualTo(20));
        Assert.That(shown.Count(p => p.IsOverflow), Is.EqualTo(7));
        Assert.That(shown.All(p => p.Y >= 0 && p.Y + p.Height <= 384.01f), Is.True);
        Assert.That(dropped, Has.Length.EqualTo(13));
    }

    [Test]
    public void MultiLineCommentIsTallerThanOneRow()
    {
        PlacedComment placed = DanmakuLayout.Arrange([Scroll(0, "一\n二\n三")], s_settings, Measure).Single();
        Assert.That(placed.Height, Is.EqualTo(24 * 1.2f * 3).Within(0.001));
    }
}

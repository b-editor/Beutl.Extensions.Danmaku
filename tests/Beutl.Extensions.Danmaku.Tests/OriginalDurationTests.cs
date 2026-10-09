using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Danmaku.Tests;

// The timeline's "元の長さに変更" and the clamp-to-original-length setting read these values through Element.
public class OriginalDurationTests
{
    private static DanmakuCommentList Comments(string content) => new(CommentParser.Parse(content));

    [Test]
    public void LastsUntilTheLastCommentLeaves()
    {
        var drawable = new DanmakuDrawable();
        // A scrolling comment at 1 s leaves at 1 + 4 * 3/4 = 4 s; a fixed one at 2 s leaves at 5 s.
        drawable.Comments.CurrentValue = Comments("1.0\t\t流れる\n2.0\tue\t固定");
        var element = new Element { Length = TimeSpan.FromSeconds(1) };
        element.AddObject(drawable);

        Assert.That(element.TryGetOriginalDuration(out TimeSpan duration), Is.True);
        Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(5)));

        drawable.TimeOffset.CurrentValue = TimeSpan.FromSeconds(1);
        drawable.ScrollDuration.CurrentValue = 8f;
        Assert.That(element.TryGetOriginalDuration(out duration), Is.True);
        // The scrolling comment now leaves at 1 + 8 * 3/4 = 7 s, plus the 1 s offset.
        Assert.That(duration, Is.EqualTo(TimeSpan.FromSeconds(8)));
    }

    [Test]
    public void HasNoOriginalDurationWithoutComments()
    {
        var drawable = new DanmakuDrawable();
        Assert.That(drawable.HasOriginalDuration(), Is.False);
        Assert.That(drawable.TryGetOriginalDuration(out _), Is.False);

        drawable.Comments.CurrentValue = Comments("1.0\t\tある");
        Assert.That(drawable.HasOriginalDuration(), Is.True);

        drawable.Comments.CurrentValue = Comments("# no comments");
        Assert.That(drawable.HasOriginalDuration(), Is.False);
    }
}

using System.Text.Json;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Media;

namespace Beutl.Extensions.Danmaku.Tests;

// The comment list is the value the element stores, so edits, equality and its JSON form decide undo and saving.
public class CommentListTests
{
    private static DanmakuComment At(double seconds, string text) => new(TimeSpan.FromSeconds(seconds), text);

    [Test]
    public void KeepsCommentsSortedAndSameTimeCommentsInOrder()
    {
        var list = new DanmakuCommentList([At(3, "c"), At(1, "a1"), At(1, "a2"), At(2, "b")]);

        Assert.That(list.Items.Select(c => c.Text), Is.EqualTo(new[] { "a1", "a2", "b", "c" }));
    }

    [Test]
    public void EditsReturnNewListsAndLeaveTheOriginalAlone()
    {
        var original = new DanmakuCommentList([At(1, "a"), At(2, "b"), At(3, "c")]);

        DanmakuCommentList added = original.Add(At(1.5, "new"));
        DanmakuCommentList replaced = original.Replace(0, At(4, "moved"));
        DanmakuCommentList removed = original.RemoveAt([0, 2]);

        Assert.That(original.Items.Select(c => c.Text), Is.EqualTo(new[] { "a", "b", "c" }));
        Assert.That(added.Items.Select(c => c.Text), Is.EqualTo(new[] { "a", "new", "b", "c" }));
        // A time edit moves the comment to its new place.
        Assert.That(replaced.Items.Select(c => c.Text), Is.EqualTo(new[] { "b", "c", "moved" }));
        Assert.That(removed.Items.Select(c => c.Text), Is.EqualTo(new[] { "b" }));
    }

    [Test]
    public void EqualityComparesTheComments()
    {
        var a = new DanmakuCommentList([At(1, "a"), At(2, "b")]);
        var b = new DanmakuCommentList([At(2, "b"), At(1, "a")]);
        var c = new DanmakuCommentList([At(1, "a"), At(2, "B")]);

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
        Assert.That(a, Is.Not.EqualTo(c));
        Assert.That(DanmakuCommentList.Empty, Is.EqualTo(new DanmakuCommentList([])));
    }

    [Test]
    public void JsonKeepsEveryAttributeInACompactForm()
    {
        var list = new DanmakuCommentList(
        [
            new DanmakuComment(TimeSpan.FromSeconds(1.5), "上の\n二行", CommentPosition.Top, CommentSize.Big, Color.FromRgb(255, 0, 0)),
            new DanmakuComment(TimeSpan.FromMilliseconds(61_234), "小さい", CommentPosition.Bottom, CommentSize.Small),
            At(70, "普通"),
        ]);

        string json = JsonSerializer.Serialize(list);
        DanmakuCommentList? restored = JsonSerializer.Deserialize<DanmakuCommentList>(json);

        Assert.That(json, Does.StartWith("""[[1.5,"ue big #FF0000","""));
        Assert.That(restored, Is.EqualTo(list));
    }

    [Test]
    public void JsonReadsNullAsEmptyAndRejectsOtherShapes()
    {
        Assert.That(JsonSerializer.Deserialize<DanmakuCommentList>("null"), Is.Null.Or.EqualTo(DanmakuCommentList.Empty));
        Assert.That(() => JsonSerializer.Deserialize<DanmakuCommentList>("""{"a":1}"""), Throws.InstanceOf<JsonException>());
        Assert.That(() => JsonSerializer.Deserialize<DanmakuCommentList>("""[[1,"","a","extra"]]"""), Throws.InstanceOf<JsonException>());
    }

    [TestCase(0, "0:00.000")]
    [TestCase(62.5, "1:02.500")]
    [TestCase(3725.25, "1:02:05.250")]
    public void FormatsTimesTheParserReadsBack(double seconds, string expected)
    {
        string text = CommentWriter.FormatTime(TimeSpan.FromSeconds(seconds));

        Assert.That(text, Is.EqualTo(expected));
        Assert.That(CommentParser.TryParseTime(text, out TimeSpan parsed), Is.True);
        Assert.That(parsed, Is.EqualTo(TimeSpan.FromSeconds(seconds)));
    }

    [Test]
    public void TextExportReadsBackTheSameComments()
    {
        var comments = new DanmakuCommentList(
        [
            new DanmakuComment(TimeSpan.FromSeconds(1.25), "一行目\n二行目", CommentPosition.Top, CommentSize.Big, Color.FromRgb(0x33, 0x99, 0xFF)),
            new DanmakuComment(TimeSpan.FromSeconds(3725), "一時間後", CommentPosition.Bottom, CommentSize.Small),
            At(2, "カンマ, を含む"),
        ]);

        string text = CommentWriter.ToText(comments.Items);

        Assert.That(text, Does.StartWith("# "));
        Assert.That(new DanmakuCommentList(CommentParser.Parse(text)), Is.EqualTo(comments));
    }

    [Test]
    public void TextExportTurnsTabsIntoSpaces()
    {
        string text = CommentWriter.ToText([At(1, "a\tb")]);

        Assert.That(CommentParser.Parse(text).Single().Text, Is.EqualTo("a b"));
    }

    [Test]
    public void FileIsWrittenWithABomAndReadsBack()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"export-{Guid.NewGuid():N}.txt");
        var comments = new DanmakuCommentList([At(1, "書き出し"), new DanmakuComment(TimeSpan.FromSeconds(2), "上", CommentPosition.Top)]);

        CommentFile.Write(path, comments.Items);

        Assert.That(File.ReadAllBytes(path).Take(3), Is.EqualTo(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.That(new DanmakuCommentList(CommentFile.Read(path)), Is.EqualTo(comments));
    }

    [TestCase("#FF0000", 255, 0, 0)]
    [TestCase("red", 255, 0, 0)]
    [TestCase(" marineblue ", 0x33, 0x99, 0xFF)]
    public void ParsesColourNamesAndHex(string word, int r, int g, int b)
    {
        Assert.That(CommentCommands.TryParseColor(word, out Color color), Is.True);
        Assert.That((color.R, color.G, color.B), Is.EqualTo(((byte)r, (byte)g, (byte)b)));
    }

    [TestCase("")]
    [TestCase("#FF00")]
    [TestCase("reddish")]
    public void RejectsUnknownColours(string word)
    {
        Assert.That(CommentCommands.TryParseColor(word, out _), Is.False);
    }
}

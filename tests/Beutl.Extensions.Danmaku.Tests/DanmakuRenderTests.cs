using System.Text.Json.Nodes;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.Media;
using Beutl.ProjectSystem;
using Beutl.Serialization;

namespace Beutl.Extensions.Danmaku.Tests;

// Renders the drawable through Beutl's own scene pipeline on a 1920x1080 scene.
public class DanmakuRenderTests
{
    private static DanmakuDrawable CreateDrawable(string comments)
    {
        var drawable = new DanmakuDrawable();
        drawable.Comments.Value = new DanmakuCommentList(CommentParser.Parse(comments));
        return drawable;
    }

    private static int Count(Bitmap frame, int x0, int y0, int x1, int y1, Func<(byte R, byte G, byte B, byte A), bool> predicate)
    {
        int count = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                if (predicate(HeadlessScene.GetPixel(frame, x, y)))
                    count++;
            }
        }

        return count;
    }

    private static bool IsRed((byte R, byte G, byte B, byte A) p) => p.A > 200 && p.R > 200 && p.G < 60 && p.B < 60;

    private static bool IsBlue((byte R, byte G, byte B, byte A) p) => p.A > 200 && p.B > 200 && p.R < 60 && p.G < 60;

    private static bool IsWhite((byte R, byte G, byte B, byte A) p) => p.A > 200 && p.R > 230 && p.G > 230 && p.B > 230;

    private static bool IsOutline((byte R, byte G, byte B, byte A) p) => p.A > 200 && p.R < 40 && p.G < 40 && p.B < 40;

    [Test]
    public void ScrollingAndFixedCommentsAppearWhereExpected()
    {
        string comments = """
            2.0	red	流れる赤いコメント
            2.0	ue blue	上に固定
            2.0	shita	下に固定
            """;
        Scene scene = HeadlessScene.Create((CreateDrawable(comments), TimeSpan.FromSeconds(10)));

        using Bitmap frame = HeadlessScene.Render(scene, TimeSpan.FromSeconds(2.5));
        HeadlessScene.SaveForInspection(frame, "danmaku.png");

        // A quarter of the way through its 4 s pass, the scrolling comment is in the right half.
        Assert.That(Count(frame, 960, 0, 1920, 120, IsRed), Is.GreaterThan(500));
        Assert.That(Count(frame, 0, 0, 960, 1080, IsRed), Is.Zero);
        // Fixed comments are centred at the top and the bottom, white text with a black outline.
        Assert.That(Count(frame, 760, 0, 1160, 120, IsBlue), Is.GreaterThan(500));
        Assert.That(Count(frame, 760, 960, 1160, 1080, IsWhite), Is.GreaterThan(500));
        Assert.That(Count(frame, 760, 960, 1160, 1080, IsOutline), Is.GreaterThan(200));
    }

    [Test]
    public void ScrollingCommentMovesLeftOverTime()
    {
        string comments = "2.0\tred\tうごく";
        Scene scene = HeadlessScene.Create((CreateDrawable(comments), TimeSpan.FromSeconds(10)));

        using Bitmap early = HeadlessScene.Render(scene, TimeSpan.FromSeconds(2.0));
        using Bitmap late = HeadlessScene.Render(scene, TimeSpan.FromSeconds(4.0));

        Assert.That(LeftmostRed(late), Is.LessThan(LeftmostRed(early) - 600));

        static int LeftmostRed(Bitmap frame)
        {
            for (int x = 0; x < 1920; x++)
            {
                for (int y = 0; y < 120; y++)
                {
                    if (IsRed(HeadlessScene.GetPixel(frame, x, y)))
                        return x;
                }
            }

            return int.MaxValue;
        }
    }

    [Test]
    public void NothingIsDrawnAfterAllCommentsLeave()
    {
        string comments = "1.0\tred\tおわり\n1.0\tue\t固定";
        Scene scene = HeadlessScene.Create((CreateDrawable(comments), TimeSpan.FromSeconds(10)));

        using Bitmap frame = HeadlessScene.Render(scene, TimeSpan.FromSeconds(6));

        Assert.That(Count(frame, 0, 0, 1920, 1080, p => p.A > 0), Is.Zero);
    }

    [Test]
    public void TimeOffsetDelaysComments()
    {
        string comments = "0.0\tue blue\tずらす";
        DanmakuDrawable drawable = CreateDrawable(comments);
        drawable.TimeOffset.Value = MediaTime.FromSeconds(5);
        Scene scene = HeadlessScene.Create((drawable, TimeSpan.FromSeconds(10)));

        using Bitmap before = HeadlessScene.Render(scene, TimeSpan.FromSeconds(1));
        using Bitmap after = HeadlessScene.Render(scene, TimeSpan.FromSeconds(6));

        Assert.That(Count(before, 760, 0, 1160, 120, IsBlue), Is.Zero);
        Assert.That(Count(after, 760, 0, 1160, 120, IsBlue), Is.GreaterThan(200));
    }

    [Test]
    public void SurvivesSerializationRoundTrip()
    {
        string comments = "1.0\t\tテスト";
        var element = new Element { Length = { Value = MediaTime.FromSeconds(5) } };
        DanmakuDrawable original = CreateDrawable(comments);
        original.FontScale.Value = 150f;
        element.AddObject(original);

        JsonObject json = CoreSerializer.SerializeToJsonObject(element);
        var restored = (DanmakuDrawable)((Element)CoreSerializer.DeserializeFromJsonObject(json, typeof(Element))).Objects.Single();

        Assert.That(restored.Comments.Value, Is.EqualTo(original.Comments.Value));
        Assert.That(restored.FontScale.Value, Is.EqualTo(150f));
    }

    [TestCase(1.25)]
    [TestCase(-2.5)]
    public void Preview8TimesAndCommentsSurviveSavingAsPreview9(double offset)
    {
        var element = new Element { Length = { Value = MediaTime.FromSeconds(10) } };
        DanmakuDrawable original = CreateDrawable("1.25\tue red\t保存されたコメント");
        element.AddObject(original);
        JsonObject json = CoreSerializer.SerializeToJsonObject(element);
        json["minAppVersion"] = "2.0.0-preview.8";
        json["Start"] = "00:00:01";
        json["Length"] = "00:00:10";
        json["Objects"]![0]!["TimeOffset"] = TimeSpan.FromSeconds(offset).ToString("c", System.Globalization.CultureInfo.InvariantCulture);
        var uri = new Uri(Path.Combine(TestContext.CurrentContext.WorkDirectory, $"preview8-{Guid.NewGuid():N}.belm"));
        File.WriteAllText(uri.LocalPath, json.ToJsonString());

        Element restored = CoreSerializer.RestoreFromUri<Element>(uri);
        var drawable = restored.Objects.OfType<DanmakuDrawable>().Single();
        Assert.That(drawable.TimeOffset.Value, Is.EqualTo(MediaTime.FromSeconds(offset)));
        Assert.That(drawable.Comments.Value, Is.EqualTo(original.Comments.Value));
        Assert.That(restored.Start.Value, Is.EqualTo(MediaTime.FromSeconds(1)));
        Assert.That(restored.Length.Value, Is.EqualTo(MediaTime.FromSeconds(10)));

        CoreSerializer.StoreToUri(restored, uri);
        JsonNode saved = JsonNode.Parse(File.ReadAllText(uri.LocalPath))!;
        Assert.That(saved["minAppVersion"]!.GetValue<string>(), Is.EqualTo("2.0.0-preview.9"));
        Assert.That(saved["Objects"]![0]!["TimeOffset"]!.GetValue<long>(), Is.EqualTo(MediaTime.FromSeconds(offset).Flicks));
        Assert.That(saved["Objects"]![0]!["Comments"]![0]![0]!.GetValue<double>(), Is.EqualTo(1.25));
        var reread = CoreSerializer.RestoreFromUri<Element>(uri).Objects.OfType<DanmakuDrawable>().Single();
        Assert.That(reread.TimeOffset.Value, Is.EqualTo(drawable.TimeOffset.Value));
        Assert.That(reread.Comments.Value, Is.EqualTo(original.Comments.Value));
    }

    [Test]
    public void TheBackHalfOfASplitShowsWhatTheWholeElementShowed()
    {
        const string comments = """
            0.5	red	最初のコメント
            3.0	blue	後のコメント
            """;
        Scene whole = HeadlessScene.Create((CreateDrawable(comments), TimeSpan.FromSeconds(10)));
        Scene split = HeadlessScene.Create((CreateDrawable(comments), TimeSpan.FromSeconds(10)));
        Element front = split.Children[0];
        Element back = HeadlessScene.Split(split, front, TimeSpan.FromSeconds(2));

        var backDrawable = back.Objects.OfType<DanmakuDrawable>().Single();
        Assert.That(backDrawable.TimeOffset.Value, Is.EqualTo(MediaTime.FromSeconds(-2)));
        Assert.That(front.Objects.OfType<DanmakuDrawable>().Single().TimeOffset.Value, Is.EqualTo(MediaTime.Zero));
        Assert.That(backDrawable.Comments.Value.Count, Is.EqualTo(2), "Both halves keep every comment.");

        // Fitting the back half to its content still ends where the comments end.
        whole.Children[0].Objects.OfType<DanmakuDrawable>().Single().TryGetOriginalDuration(out MediaTime wholeDuration);
        Assert.That(backDrawable.TryGetOriginalDuration(out MediaTime backDuration), Is.True);
        Assert.That(backDuration, Is.EqualTo(wholeDuration - MediaTime.FromSeconds(2)));

        // At the cut, the first comment is half way across; later, the second one has come in.
        foreach (double seconds in new[] { 2.0, 3.5 })
        {
            using Bitmap expected = HeadlessScene.Render(whole, TimeSpan.FromSeconds(seconds));
            using Bitmap actual = HeadlessScene.Render(split, TimeSpan.FromSeconds(seconds));
            HeadlessScene.AssertSameFrame(expected, actual);
        }
    }
}

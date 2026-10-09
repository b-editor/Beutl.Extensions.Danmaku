using Beutl.Extensions.Danmaku.Comments;
using Beutl.Media;

namespace Beutl.Extensions.Danmaku.Tests;

public class CommentParserTests
{
    [Test]
    public void ParsesLegacyXmlWithoutSkippingAdjacentChats()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <packet>
              <thread thread="1" />
              <chat thread="1" no="1" vpos="150" mail="184">こんにちは</chat><chat thread="1" no="2" vpos="300" mail="ue red big">上の赤</chat>
              <chat thread="1" no="3" vpos="100" mail="shita #00FF80 small">下 &amp; 緑</chat>
              <chat thread="1" no="4" vpos="200" deleted="1">削除済み</chat>
              <chat thread="1" no="5" vpos="250" mail="invisible">見えない</chat>
              <chat thread="1" no="6" vpos="260" />
            </packet>
            """;

        List<DanmakuComment> comments = CommentParser.Parse(xml);

        Assert.That(comments.Select(c => c.Text), Is.EqualTo(new[] { "下 & 緑", "こんにちは", "上の赤" }));
        Assert.That(comments[0], Has.Property(nameof(DanmakuComment.Position)).EqualTo(CommentPosition.Bottom)
            .And.Property(nameof(DanmakuComment.Size)).EqualTo(CommentSize.Small)
            .And.Property(nameof(DanmakuComment.Time)).EqualTo(TimeSpan.FromSeconds(1)));
        Assert.That(comments[0].FillColor, Is.EqualTo(Color.FromArgb(255, 0, 0xFF, 0x80)));
        Assert.That(comments[1].Position, Is.EqualTo(CommentPosition.Scroll));
        Assert.That(comments[1].FillColor, Is.EqualTo(Colors.White));
        Assert.That(comments[2], Has.Property(nameof(DanmakuComment.Position)).EqualTo(CommentPosition.Top)
            .And.Property(nameof(DanmakuComment.Size)).EqualTo(CommentSize.Big));
        Assert.That(comments[2].FillColor, Is.EqualTo(Color.FromArgb(255, 255, 0, 0)));
    }

    [Test]
    public void XmlExternalEntitiesAreNotResolved()
    {
        string secret = Path.GetTempFileName();
        File.WriteAllText(secret, "SECRET");
        try
        {
            string xml = $"""
                <?xml version="1.0"?>
                <!DOCTYPE packet [<!ENTITY xxe SYSTEM "{new Uri(secret)}">]>
                <packet><chat vpos="100">&xxe;</chat><chat vpos="200">ok</chat></packet>
                """;

            List<DanmakuComment> comments;
            try
            {
                comments = CommentParser.Parse(xml);
            }
            catch (System.Xml.XmlException)
            {
                return;
            }

            Assert.That(comments.Select(c => c.Text), Has.None.Contains("SECRET"));
        }
        finally
        {
            File.Delete(secret);
        }
    }

    [Test]
    public void ParsesNestedJsonThreads()
    {
        const string json = """
            {"meta":{"status":200},"data":{"threads":[
              {"fork":"main","comments":[
                {"id":"1","no":1,"vposMs":2500,"body":"流れる","commands":["184"]},
                {"id":"2","no":2,"vposMs":1200,"body":"上","commands":["ue","cyan"]}
              ]},
              {"fork":"easy","comments":[{"no":3,"vposMs":500,"body":"かんたん","commands":[]}]}
            ]}}
            """;

        List<DanmakuComment> comments = CommentParser.Parse(json);

        Assert.That(comments.Select(c => c.Text), Is.EqualTo(new[] { "かんたん", "上", "流れる" }));
        Assert.That(comments[1].Position, Is.EqualTo(CommentPosition.Top));
        Assert.That(comments[1].Time, Is.EqualTo(TimeSpan.FromMilliseconds(1200)));
        Assert.That(comments[1].FillColor, Is.EqualTo(Color.FromArgb(255, 0, 255, 255)));
    }

    [Test]
    public void ParsesTextLines()
    {
        const string text = """
            # 時刻	コマンド	本文
            1.5	red	一行目
            0:02.25		二行目\n改行あり
            3,ue,カンマ区切り, 本文のカンマ
            4	コマンドなし
            not-a-time	x	無視
            """;

        List<DanmakuComment> comments = CommentParser.Parse(text);

        Assert.That(comments.Select(c => c.Text),
            Is.EqualTo(new[] { "一行目", "二行目\n改行あり", "カンマ区切り, 本文のカンマ", "コマンドなし" }));
        Assert.That(comments[1].Time, Is.EqualTo(TimeSpan.FromSeconds(2.25)));
        Assert.That(comments[2].Position, Is.EqualTo(CommentPosition.Top));
        Assert.That(comments[0].FillColor, Is.EqualTo(Color.FromArgb(255, 255, 0, 0)));
        Assert.That(comments[1].Lines, Has.Length.EqualTo(2));
    }

    [TestCase("12.5", 12.5)]
    [TestCase("1:02.5", 62.5)]
    [TestCase("1:00:00", 3600)]
    public void ParsesTimes(string value, double seconds)
    {
        Assert.That(CommentParser.TryParseTime(value, out TimeSpan time), Is.True);
        Assert.That(time, Is.EqualTo(TimeSpan.FromSeconds(seconds)));
    }

    [TestCase("")]
    [TestCase("-1")]
    [TestCase("1:2:3:4")]
    [TestCase("abc")]
    public void RejectsInvalidTimes(string value)
    {
        Assert.That(CommentParser.TryParseTime(value, out _), Is.False);
    }
}

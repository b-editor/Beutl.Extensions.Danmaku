using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Beutl.Extensions.Danmaku.Comments;

namespace Beutl.Extensions.Danmaku.Tests;

// The files yt-dlp writes for niconico comments and YouTube chat replays. The samples are synthetic, shaped like
// the real files.
public class DownloadedFormatTests
{
    [Test]
    public void ReadsNiconicoCommentsAndHidesLowScoreOnes()
    {
        const string json = """
            [
              {"id":"1","no":1,"vposMs":1500,"body":"見える","commands":["ue","red"],"score":0},
              {"id":"2","no":2,"vposMs":2000,"body":"NGスコアで隠れる","commands":[],"score":-5000},
              {"id":"3","no":3,"vposMs":2500,"body":"ぎりぎり見える","commands":["big"],"score":-4799},
              {"id":"4","no":4,"vposMs":3000,"body":"削除済み","commands":[],"isDeleted":true}
            ]
            """;

        List<DanmakuComment> comments = CommentParser.Parse(json);

        Assert.That(comments.Select(c => c.Text), Is.EqualTo(new[] { "見える", "ぎりぎり見える" }));
        Assert.That(comments[0].Time, Is.EqualTo(TimeSpan.FromSeconds(1.5)));
        Assert.That(comments[0].Position, Is.EqualTo(CommentPosition.Top));
        Assert.That(comments[1].Size, Is.EqualTo(CommentSize.Big));
    }

    private static readonly JsonSerializerOptions s_rawUtf8 = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // One line of yt-dlp's *.live_chat.json: a replay action holding one chat item.
    private static string ChatLine(long offsetMs, string renderer, string body)
    {
        var line = new JsonObject
        {
            ["replayChatItemAction"] = new JsonObject
            {
                ["actions"] = new JsonArray(new JsonObject
                {
                    ["addChatItemAction"] = new JsonObject
                    {
                        ["item"] = new JsonObject { [renderer] = JsonNode.Parse(body) },
                        ["clientId"] = "x",
                    },
                }),
                ["videoOffsetTimeMsec"] = offsetMs.ToString(CultureInfo.InvariantCulture),
            },
            ["isLive"] = true,
        };
        return line.ToJsonString(s_rawUtf8);
    }

    [Test]
    public void ReadsAYouTubeChatReplay()
    {
        string content = string.Join('\n',
            ChatLine(5_000, "liveChatTextMessageRenderer", """
                {"message":{"runs":[
                  {"text":"こんにちは"},
                  {"emoji":{"emojiId":"👋","shortcuts":[":wave:"]}},
                  {"emoji":{"emojiId":"UCxxx/abc","isCustomEmoji":true}}
                ]},"authorName":{"simpleText":"視聴者"}}
                """),
            ChatLine(7_250, "liveChatPaidMessageRenderer", """
                {"purchaseAmountText":{"simpleText":"¥1,000"},
                 "message":{"runs":[{"text":"応援してます"}]},"bodyBackgroundColor":4294953512}
                """),
            // Chat posted before the stream started has a negative offset and never shows on the video.
            ChatLine(-3_000, "liveChatTextMessageRenderer", """{"message":{"runs":[{"text":"待機中"}]}}"""),
            "not json",
            "");

        List<DanmakuComment> comments = CommentParser.Parse(content);

        Assert.That(comments.Select(c => c.Text), Is.EqualTo(new[] { "こんにちは👋", "¥1,000 応援してます" }));
        Assert.That(comments[0].Time, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(comments[0].Color, Is.Null);
        Assert.That(comments[1].Time, Is.EqualTo(TimeSpan.FromSeconds(7.25)));
        // 4294953512 is 0xFFFFCA28, the yellow of a mid-tier Super Chat.
        Assert.That(CommentCommands.FormatColor(comments[1].Color!.Value), Is.EqualTo("#FFCA28"));
    }

    [Test]
    public void SkipsChatLinesWithoutText()
    {
        string content = ChatLine(1_000, "liveChatTextMessageRenderer",
            """{"message":{"runs":[{"emoji":{"emojiId":"UC/x","isCustomEmoji":true}}]}}""");

        Assert.That(CommentParser.Parse(content), Is.Empty);
    }
}

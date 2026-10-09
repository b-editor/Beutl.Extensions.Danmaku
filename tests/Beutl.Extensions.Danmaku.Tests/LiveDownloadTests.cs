using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Download;

namespace Beutl.Extensions.Danmaku.Tests;

// Talks to the real sites, so it only runs when asked:
//   BEUTL_DANMAKU_LIVE_URLS="https://www.nicovideo.jp/watch/sm9 https://www.twitch.tv/videos/…" dotnet test
// Twitch VODs expire and chat replays depend on the stream, so the URLs are supplied rather than fixed.
public class LiveDownloadTests
{
    private static IEnumerable<string> Urls() =>
        (Environment.GetEnvironmentVariable("BEUTL_DANMAKU_LIVE_URLS") ?? "")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Test]
    public async Task DownloadsCommentsFromTheGivenUrls()
    {
        string[] urls = Urls().ToArray();
        if (urls.Length == 0)
            Assert.Ignore("Set BEUTL_DANMAKU_LIVE_URLS to run against the real sites.");

        var downloader = new CommentDownloader();
        foreach (string url in urls)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            List<DanmakuComment> comments = await downloader.DownloadAsync(url, null, timeout.Token);

            TestContext.Out.WriteLine(
                $"{CommentSourceDetector.Detect(url)!.SiteName}: {comments.Count} comments in {watch.Elapsed.TotalSeconds:F1} s, "
                + $"{CommentWriter.FormatTime(comments.FirstOrDefault()?.Time ?? default)} – {CommentWriter.FormatTime(comments.LastOrDefault()?.Time ?? default)}, "
                + $"{comments.Count(c => c.Position != CommentPosition.Scroll)} fixed, {comments.Count(c => c.Color != null)} coloured");
            Assert.That(comments, Is.Not.Empty, url);
            Assert.That(comments.Select(c => c.Time), Is.Ordered, url);
        }
    }
}

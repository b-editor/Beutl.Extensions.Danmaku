using System.Net;
using System.Text;
using System.Text.Json;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Download;

namespace Beutl.Extensions.Danmaku.Tests;

// Everything here runs offline: Twitch answers come from a fake HTTP handler and yt-dlp from a fake script.
public class DownloadTests
{
    [TestCase("https://www.nicovideo.jp/watch/sm9", CommentSite.Niconico, "sm9")]
    [TestCase("nicovideo.jp/watch/so12345?ref=top", CommentSite.Niconico, "so12345")]
    [TestCase("https://sp.nicovideo.jp/watch/sm500", CommentSite.Niconico, "sm500")]
    [TestCase("https://nico.ms/sm9", CommentSite.Niconico, "sm9")]
    [TestCase("https://www.youtube.com/watch?v=dQw4w9WgXcQ", CommentSite.YouTube, "dQw4w9WgXcQ")]
    [TestCase("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=10", CommentSite.YouTube, "dQw4w9WgXcQ")]
    [TestCase("https://youtu.be/dQw4w9WgXcQ?si=abc", CommentSite.YouTube, "dQw4w9WgXcQ")]
    [TestCase("https://www.youtube.com/live/dQw4w9WgXcQ", CommentSite.YouTube, "dQw4w9WgXcQ")]
    [TestCase("  https://m.youtube.com/shorts/dQw4w9WgXcQ  ", CommentSite.YouTube, "dQw4w9WgXcQ")]
    [TestCase("https://www.twitch.tv/videos/2245501234", CommentSite.Twitch, "2245501234")]
    [TestCase("https://www.twitch.tv/somechannel/v/2245501234", CommentSite.Twitch, "2245501234")]
    public void DetectsSupportedVideoUrls(string url, CommentSite site, string id)
    {
        CommentSource? source = CommentSourceDetector.Detect(url);

        Assert.That(source, Is.Not.Null);
        Assert.That(source!.Site, Is.EqualTo(site));
        Assert.That(source.VideoId, Is.EqualTo(id));
        Assert.That(source.Url, Is.EqualTo(url.Trim()));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("https://www.youtube.com/@channel")]
    [TestCase("https://www.twitch.tv/somechannel")]
    [TestCase("https://example.com/watch/sm9")]
    [TestCase("https://evil.example/?u=https://www.nicovideo.jp/watch/sm9")]
    public void RejectsOtherUrls(string? url)
    {
        Assert.That(CommentSourceDetector.Detect(url), Is.Null);
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    [Test]
    public void LocatePrefersTheCopyInTheBeutlHome()
    {
        string home = NewDirectory();
        string bin = NewDirectory();
        Directory.CreateDirectory(Path.Combine(home, "yt-dlp"));
        File.WriteAllText(Path.Combine(home, "yt-dlp", ExecutableName), "");
        File.WriteAllText(Path.Combine(bin, ExecutableName), "");

        Assert.That(YtDlpCommentDownloader.Locate(home, bin), Is.EqualTo(Path.Combine(home, "yt-dlp", ExecutableName)));
    }

    [Test]
    public void LocateFallsBackToPath()
    {
        string home = NewDirectory();
        string empty = NewDirectory();
        string bin = NewDirectory();
        File.WriteAllText(Path.Combine(bin, ExecutableName), "");

        string path = string.Join(Path.PathSeparator, "", empty, bin);
        Assert.That(YtDlpCommentDownloader.Locate(home, path), Is.EqualTo(Path.Combine(bin, ExecutableName)));
        Assert.That(YtDlpCommentDownloader.Locate(home, empty), Is.Null);
    }

    [Test]
    public void YtDlpIsAskedOnlyForTheCommentTrack()
    {
        var niconico = new CommentSource(CommentSite.Niconico, "https://www.nicovideo.jp/watch/sm9", "sm9");
        var youtube = new CommentSource(CommentSite.YouTube, "https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ");

        IReadOnlyList<string> args = YtDlpCommentDownloader.BuildArguments(niconico, "/tmp/out");

        Assert.That(args, Does.Contain("--skip-download"));
        Assert.That(args, Does.Contain("--no-playlist"));
        Assert.That(args[args.ToList().IndexOf("--sub-langs") + 1], Is.EqualTo("comments"));
        Assert.That(args[^1], Is.EqualTo(niconico.Url));
        Assert.That(YtDlpCommentDownloader.BuildArguments(youtube, "/tmp/out"), Does.Contain("live_chat"));
        Assert.That(() => YtDlpCommentDownloader.BuildArguments(
                new CommentSource(CommentSite.Twitch, "https://www.twitch.tv/videos/1", "1"), "/tmp/out"),
            Throws.InstanceOf<ArgumentOutOfRangeException>());
    }

    // A stand-in for yt-dlp: writes the subtitle file named by the -o template, or fails like yt-dlp does.
    private static string FakeYtDlp(string body)
    {
        if (OperatingSystem.IsWindows())
            throw new IgnoreException("The fake yt-dlp is a shell script.");

        string path = Path.Combine(NewDirectory(), "yt-dlp");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Test]
    public async Task YtDlpDownloadReadsTheWrittenCommentFile()
    {
        string script = FakeYtDlp("""
            while [ "$#" -gt 0 ]; do
              if [ "$1" = "-o" ]; then out="$2"; fi
              shift
            done
            dir=$(dirname "$out")
            echo "[niconico] sm9: Downloading comments"
            printf '%s' '[{"vposMs":1000,"body":"いち","commands":["ue"]},{"vposMs":500,"body":"はん","commands":[]}]' > "$dir/sm9.comments.json"
            """);
        var progress = new List<string>();
        var source = new CommentSource(CommentSite.Niconico, "https://www.nicovideo.jp/watch/sm9", "sm9");

        List<DanmakuComment> comments = await new YtDlpCommentDownloader(script)
            .DownloadAsync(source, new SyncProgress(progress), CancellationToken.None);

        Assert.That(comments.Select(c => c.Text), Is.EqualTo(new[] { "はん", "いち" }));
        Assert.That(comments[1].Position, Is.EqualTo(CommentPosition.Top));
        Assert.That(progress, Does.Contain("[niconico] sm9: Downloading comments"));
    }

    [Test]
    public void YtDlpFailureCarriesItsErrorLine()
    {
        string script = FakeYtDlp("""
            echo "WARNING: something minor" >&2
            echo "ERROR: [niconico] sm1: This video is only available to members" >&2
            exit 1
            """);
        var source = new CommentSource(CommentSite.Niconico, "https://www.nicovideo.jp/watch/sm1", "sm1");

        var ex = Assert.ThrowsAsync<CommentDownloadException>(() =>
            new YtDlpCommentDownloader(script).DownloadAsync(source, null, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("コメントを取得できませんでした"));
        Assert.That(ex.Message, Does.Contain("ERROR: [niconico] sm1: This video is only available to members"));
    }

    [Test]
    public void YtDlpWithoutAChatReplayExplainsWhy()
    {
        string script = FakeYtDlp("echo 'WARNING: There are no subtitles for the requested languages' >&2");
        var source = new CommentSource(CommentSite.YouTube, "https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ");

        var ex = Assert.ThrowsAsync<CommentDownloadException>(() =>
            new YtDlpCommentDownloader(script).DownloadAsync(source, null, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("チャットのリプレイがありません"));
        Assert.That(ex.Message, Does.Not.Contain("WARNING"));
    }

    [Test]
    public void CancellingStopsYtDlp()
    {
        string script = FakeYtDlp("sleep 30");
        var source = new CommentSource(CommentSite.Niconico, "https://www.nicovideo.jp/watch/sm9", "sm9");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.That(() => new YtDlpCommentDownloader(script).DownloadAsync(source, null, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void TwitchRequestUsesThePersistedCommentQuery()
    {
        using JsonDocument first = JsonDocument.Parse(TwitchChatDownloader.BuildRequest("123", null));
        using JsonDocument next = JsonDocument.Parse(TwitchChatDownloader.BuildRequest("123", "abc"));

        Assert.That(first.RootElement.GetProperty("operationName").GetString(), Is.EqualTo("VideoCommentsByOffsetOrCursor"));
        Assert.That(first.RootElement.GetProperty("variables").GetProperty("videoID").GetString(), Is.EqualTo("123"));
        Assert.That(first.RootElement.GetProperty("variables").GetProperty("contentOffsetSeconds").GetInt32(), Is.Zero);
        Assert.That(next.RootElement.GetProperty("variables").GetProperty("cursor").GetString(), Is.EqualTo("abc"));
        Assert.That(next.RootElement.GetProperty("variables").TryGetProperty("contentOffsetSeconds", out _), Is.False);
        Assert.That(first.RootElement.GetProperty("extensions").GetProperty("persistedQuery").GetProperty("sha256Hash").GetString(),
            Has.Length.EqualTo(64));
    }

    private static string TwitchPage(bool hasNext, params (string Cursor, int Offset, string[] Fragments)[] edges)
    {
        var page = new
        {
            data = new
            {
                video = new
                {
                    id = "123",
                    comments = new
                    {
                        edges = edges.Select(e => new
                        {
                            cursor = e.Cursor,
                            node = new
                            {
                                contentOffsetSeconds = e.Offset,
                                message = new { fragments = e.Fragments.Select(f => new { text = f }).ToArray() },
                            },
                        }).ToArray(),
                        pageInfo = new { hasNextPage = hasNext },
                    },
                },
            },
        };
        // Twitch answers a single query with an array of one result.
        return JsonSerializer.Serialize(new[] { page });
    }

    [Test]
    public void TwitchPageJoinsFragmentsAndKeepsTheLastCursor()
    {
        using JsonDocument document = JsonDocument.Parse(TwitchPage(true,
            ("c1", 3, ["Kappa", " ", "こんにちは"]),
            ("c2", 4, [" "]),
            ("c3", 4, ["GG"])));

        TwitchChatPage page = TwitchChatDownloader.ParsePage(document.RootElement);

        Assert.That(page.Messages, Is.EqualTo(new[] { (3, "Kappa こんにちは"), (4, "GG") }));
        Assert.That(page.Cursor, Is.EqualTo("c3"));
        Assert.That(page.HasNextPage, Is.True);
    }

    [Test]
    public void TwitchErrorsBecomeReadableExceptions()
    {
        using JsonDocument error = JsonDocument.Parse("""{"errors":[{"message":"failed integrity check"}]}""");
        using JsonDocument missing = JsonDocument.Parse("""{"data":{"video":null}}""");

        Assert.That(() => TwitchChatDownloader.ParsePage(error.RootElement),
            Throws.InstanceOf<CommentDownloadException>().With.Message.Contains("failed integrity check"));
        Assert.That(() => TwitchChatDownloader.ParsePage(missing.RootElement),
            Throws.InstanceOf<CommentDownloadException>().With.Message.Contains("見つかりません"));
    }

    [Test]
    public void TwitchMessagesOfOneSecondAreSpreadAcrossIt()
    {
        List<DanmakuComment> comments = TwitchChatDownloader.SpreadWithinSeconds([(2, "a"), (1, "x"), (2, "b"), (2, "c")]);

        Assert.That(comments.Select(c => c.Text), Is.EqualTo(new[] { "x", "a", "b", "c" }));
        Assert.That(comments.Select(c => c.Time.TotalMilliseconds), Is.EqualTo(new[] { 1000d, 2000, 2333, 2667 }));
    }

    [Test]
    public async Task TwitchDownloadFollowsCursorsUntilTheLastPage()
    {
        var handler = new FakeHandler(
            TwitchPage(true, ("c1", 1, ["one"]), ("c2", 1, ["two"])),
            TwitchPage(false, ("c3", 5, ["three"])));
        using var http = new HttpClient(handler);
        var progress = new List<string>();

        List<DanmakuComment> comments = await new CommentDownloader(() => null, http)
            .DownloadAsync("https://www.twitch.tv/videos/123", new SyncProgress(progress), CancellationToken.None);

        Assert.That(comments.Select(c => (c.Time.TotalSeconds, c.Text)),
            Is.EqualTo(new[] { (1.0, "one"), (1.5, "two"), (5.0, "three") }));
        Assert.That(handler.Requests, Has.Count.EqualTo(2));
        Assert.That(handler.Requests[0].ClientId, Is.Not.Empty);
        Assert.That(handler.Requests[0].Body, Does.Contain("\"contentOffsetSeconds\":0"));
        Assert.That(handler.Requests[1].Body, Does.Contain("\"cursor\":\"c2\""));
        Assert.That(progress, Has.Some.Contains("3 件"));
    }

    [Test]
    public void TwitchHttpFailureIsReported()
    {
        using var http = new HttpClient(new FakeHandler(HttpStatusCode.ServiceUnavailable));

        Assert.That(() => new CommentDownloader(() => null, http).DownloadAsync("https://www.twitch.tv/videos/123", null, CancellationToken.None),
            Throws.InstanceOf<CommentDownloadException>().With.Message.Contains("Twitch に接続できませんでした"));
    }

    [Test]
    public void MissingYtDlpIsExplained()
    {
        Assert.That(() => new CommentDownloader(() => null).DownloadAsync("https://www.nicovideo.jp/watch/sm9", null, CancellationToken.None),
            Throws.InstanceOf<CommentDownloadException>().With.Message.Contains("yt-dlp が見つかりません"));
        Assert.That(() => new CommentDownloader(() => null).DownloadAsync("https://example.com/", null, CancellationToken.None),
            Throws.InstanceOf<CommentDownloadException>().With.Message.Contains("対応していない URL"));
    }

    // Progress<T> posts to the thread pool; this one records synchronously so the test can read it at once.
    private sealed class SyncProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value)
        {
            lock (messages)
                messages.Add(value);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<string> _pages;
        private readonly HttpStatusCode _status;

        public FakeHandler(params string[] pages)
        {
            _pages = new Queue<string>(pages);
            _status = HttpStatusCode.OK;
        }

        public FakeHandler(HttpStatusCode status)
        {
            _pages = new Queue<string>();
            _status = status;
        }

        public List<(string ClientId, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            string clientId = request.Headers.TryGetValues("Client-ID", out IEnumerable<string>? values) ? values.Single() : "";
            Requests.Add((clientId, body));

            if (_status != HttpStatusCode.OK)
                return new HttpResponseMessage(_status);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_pages.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}

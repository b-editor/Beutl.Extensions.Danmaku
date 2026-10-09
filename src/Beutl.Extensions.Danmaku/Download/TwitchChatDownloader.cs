using System.Text;
using System.Text.Json;
using Beutl.Extensions.Danmaku.Comments;

namespace Beutl.Extensions.Danmaku.Download;

/// <summary>
/// Downloads a Twitch VOD's chat replay through Twitch's unofficial GraphQL endpoint, the same request the
/// Twitch website and TwitchDownloader make. Twitch may change or block it at any time.
/// </summary>
public sealed class TwitchChatDownloader(HttpClient httpClient)
{
    private const string Endpoint = "https://gql.twitch.tv/gql";
    private const string ClientId = "kd1unb4b3q4t58fwlpcbzcbnm76a8fp";
    private const string QueryHash = "b70a3591ff0f4e0313d126c6a1502d79a1c02baebb288227c582044aa76adf6a";

    // Guards against a cursor that never ends; a ten-hour stream with a busy chat is far below this.
    private const int MaxPages = 20_000;

    public async Task<List<DanmakuComment>> DownloadAsync(string videoId, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var messages = new List<(int Offset, string Text)>();
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(BuildRequest(videoId, cursor), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Client-ID", ClientId);

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            TwitchChatPage parsed = ParsePage(document.RootElement);
            messages.AddRange(parsed.Messages);
            if (parsed.Messages.Count > 0)
                progress?.Report(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{messages.Count:N0} 件（{CommentWriter.FormatTime(TimeSpan.FromSeconds(parsed.Messages[^1].Offset))} まで）"));

            if (!parsed.HasNextPage || parsed.Cursor == null || parsed.Cursor == cursor)
                break;
            cursor = parsed.Cursor;
        }

        return SpreadWithinSeconds(messages);
    }

    public static string BuildRequest(string videoId, string? cursor)
    {
        object variables = cursor == null
            ? new { videoID = videoId, contentOffsetSeconds = 0 }
            : new { videoID = videoId, cursor };
        return JsonSerializer.Serialize(new
        {
            operationName = "VideoCommentsByOffsetOrCursor",
            variables,
            extensions = new { persistedQuery = new { version = 1, sha256Hash = QueryHash } },
        });
    }

    /// <exception cref="CommentDownloadException">Twitch returned an error or no such video.</exception>
    public static TwitchChatPage ParsePage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            root = root[0];

        if (root.TryGetProperty("errors", out JsonElement errors) && errors.ValueKind == JsonValueKind.Array
                                                                  && errors.GetArrayLength() > 0)
        {
            string message = errors[0].TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
            throw new CommentDownloadException($"Twitch がエラーを返しました: {message}");
        }

        if (!root.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("video", out JsonElement video) || video.ValueKind != JsonValueKind.Object)
        {
            throw new CommentDownloadException("Twitch の動画が見つかりません");
        }

        var messages = new List<(int, string)>();
        if (!video.TryGetProperty("comments", out JsonElement comments) || comments.ValueKind != JsonValueKind.Object)
            return new TwitchChatPage(messages, null, false);

        string? cursor = null;
        if (comments.TryGetProperty("edges", out JsonElement edges) && edges.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement edge in edges.EnumerateArray())
            {
                if (edge.TryGetProperty("cursor", out JsonElement c) && c.ValueKind == JsonValueKind.String)
                    cursor = c.GetString();
                if (!edge.TryGetProperty("node", out JsonElement node)
                    || !node.TryGetProperty("contentOffsetSeconds", out JsonElement offset) || !offset.TryGetInt32(out int seconds)
                    || !node.TryGetProperty("message", out JsonElement message)
                    || !message.TryGetProperty("fragments", out JsonElement fragments) || fragments.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var sb = new StringBuilder();
                foreach (JsonElement fragment in fragments.EnumerateArray())
                {
                    if (fragment.TryGetProperty("text", out JsonElement text))
                        sb.Append(text.GetString());
                }

                string body = sb.ToString().Trim();
                if (body.Length > 0)
                    messages.Add((seconds, body));
            }
        }

        bool hasNext = comments.TryGetProperty("pageInfo", out JsonElement pageInfo)
                       && pageInfo.TryGetProperty("hasNextPage", out JsonElement next) && next.ValueKind == JsonValueKind.True;
        return new TwitchChatPage(messages, cursor, hasNext);
    }

    /// <summary>
    /// Twitch reports offsets in whole seconds, so the messages of one second are spread evenly across it
    /// instead of all appearing at once.
    /// </summary>
    public static List<DanmakuComment> SpreadWithinSeconds(IReadOnlyList<(int Offset, string Text)> messages)
    {
        var comments = new List<DanmakuComment>(messages.Count);
        foreach (IGrouping<int, (int Offset, string Text)> second in messages.GroupBy(m => m.Offset).OrderBy(g => g.Key))
        {
            int count = second.Count();
            int index = 0;
            foreach ((int offset, string text) in second)
            {
                // Whole milliseconds, so the times survive the project file's 3-decimal seconds unchanged.
                double ms = Math.Round((offset + (double)index / count) * 1000);
                comments.Add(new DanmakuComment(TimeSpan.FromMilliseconds(ms), text));
                index++;
            }
        }

        return comments;
    }
}

public sealed record TwitchChatPage(IReadOnlyList<(int Offset, string Text)> Messages, string? Cursor, bool HasNextPage);

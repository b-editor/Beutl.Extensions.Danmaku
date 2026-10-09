using System.Text.RegularExpressions;

namespace Beutl.Extensions.Danmaku.Download;

public enum CommentSite
{
    Niconico,
    YouTube,
    Twitch,
}

/// <summary>A video whose comments or chat replay can be downloaded.</summary>
public sealed record CommentSource(CommentSite Site, string Url, string VideoId)
{
    public string SiteName => Site switch
    {
        CommentSite.Niconico => "ニコニコ動画",
        CommentSite.YouTube => "YouTube",
        _ => "Twitch",
    };
}

public static partial class CommentSourceDetector
{
    [GeneratedRegex(@"^(?:https?://)?(?:[\w-]+\.)?(?:nicovideo\.jp/watch/|nico\.ms/)((?:sm|nm|so)?\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Niconico();

    [GeneratedRegex(@"^(?:https?://)?(?:(?:www|m|music)\.)?(?:youtube\.com/(?:watch\?(?:.*&)?v=|live/|shorts/|embed/)|youtu\.be/)([\w-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTube();

    [GeneratedRegex(@"^(?:https?://)?(?:(?:www|m)\.)?twitch\.tv/(?:videos/|[\w]+/v(?:ideo)?/)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex Twitch();

    /// <summary>Recognises a video URL of a supported site, or returns null.</summary>
    public static CommentSource? Detect(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        url = url.Trim();
        if (Niconico().Match(url) is { Success: true } niconico)
            return new CommentSource(CommentSite.Niconico, url, niconico.Groups[1].Value);
        if (YouTube().Match(url) is { Success: true } youtube)
            return new CommentSource(CommentSite.YouTube, url, youtube.Groups[1].Value);
        if (Twitch().Match(url) is { Success: true } twitch)
            return new CommentSource(CommentSite.Twitch, url, twitch.Groups[1].Value);
        return null;
    }
}

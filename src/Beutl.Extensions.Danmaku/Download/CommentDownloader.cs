using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Properties;

namespace Beutl.Extensions.Danmaku.Download;

/// <summary>Downloads the comments of a supported video URL with the right backend for its site.</summary>
public sealed class CommentDownloader
{
    private static readonly Lazy<HttpClient> s_httpClient = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    private readonly Func<string?> _locateYtDlp;
    private readonly HttpClient _httpClient;

    public CommentDownloader(Func<string?>? locateYtDlp = null, HttpClient? httpClient = null)
    {
        _locateYtDlp = locateYtDlp ?? (() => YtDlpCommentDownloader.Locate());
        _httpClient = httpClient ?? s_httpClient.Value;
    }

    /// <exception cref="CommentDownloadException">The URL is not supported or the download failed.</exception>
    public async Task<List<DanmakuComment>> DownloadAsync(string url, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        CommentSource source = CommentSourceDetector.Detect(url)
                               ?? throw new CommentDownloadException(Strings.UnsupportedUrl);

        if (source.Site == CommentSite.Twitch)
        {
            progress?.Report(Strings.Downloading);
            try
            {
                return await new TwitchChatDownloader(_httpClient).DownloadAsync(source.VideoId, progress, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new CommentDownloadException(string.Format(Strings.TwitchConnectionFailed, ex.Message), ex);
            }
        }

        string executable = _locateYtDlp()
                            ?? throw new CommentDownloadException(
                                Strings.YtDlpNotFound);
        progress?.Report(Strings.Downloading);
        return await new YtDlpCommentDownloader(executable).DownloadAsync(source, progress, cancellationToken).ConfigureAwait(false);
    }
}

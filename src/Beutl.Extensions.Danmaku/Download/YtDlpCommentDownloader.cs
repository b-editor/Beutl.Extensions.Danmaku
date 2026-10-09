using System.Diagnostics;
using Beutl.Extensions.Danmaku.Comments;

namespace Beutl.Extensions.Danmaku.Download;

/// <summary>
/// Downloads niconico comments and YouTube live chat replays with yt-dlp, which writes them as subtitle
/// tracks named "comments" and "live_chat".
/// </summary>
public sealed class YtDlpCommentDownloader
{
    public YtDlpCommentDownloader(string executable)
    {
        Executable = executable;
    }

    public string Executable { get; }

    /// <summary>
    /// Finds yt-dlp: the copy the yt-dlp extension installs into ~/.beutl/yt-dlp, then one on PATH.
    /// </summary>
    public static string? Locate(string? beutlHome = null, string? path = null)
    {
        string name = OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";
        string home = beutlHome ?? BeutlEnvironment.GetHomeDirectoryPath();
        string installed = Path.Combine(home, "yt-dlp", name);
        if (File.Exists(installed))
            return installed;

        foreach (string directory in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
                continue;

            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string GetSubtitleLanguage(CommentSite site) => site switch
    {
        CommentSite.Niconico => "comments",
        CommentSite.YouTube => "live_chat",
        _ => throw new ArgumentOutOfRangeException(nameof(site), site, "yt-dlp では取得できないサイトです。"),
    };

    public static IReadOnlyList<string> BuildArguments(CommentSource source, string outputDirectory)
    {
        return
        [
            "--skip-download",
            "--no-playlist",
            "--no-progress",
            "--write-subs",
            "--sub-langs", GetSubtitleLanguage(source.Site),
            "-o", Path.Combine(outputDirectory, "%(id)s.%(ext)s"),
            source.Url,
        ];
    }

    public async Task<List<DanmakuComment>> DownloadAsync(CommentSource source, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string language = GetSubtitleLanguage(source.Site);
        string directory = Path.Combine(Path.GetTempPath(), $"beutl-danmaku-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var startInfo = new ProcessStartInfo(Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string argument in BuildArguments(source, directory))
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo };
            var errors = new List<string>();
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    progress?.Report(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;
                lock (errors)
                    errors.Add(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            string? file = Directory.EnumerateFiles(directory, $"*.{language}.json").FirstOrDefault();
            if (file == null)
            {
                string? reason;
                lock (errors)
                    // Only an ERROR line explains the failure; warnings (such as the missing JavaScript runtime
                    // for YouTube) appear on successful runs too and would mislead.
                    reason = errors.LastOrDefault(e => e.StartsWith("ERROR", StringComparison.Ordinal));
                string message = source.Site == CommentSite.YouTube
                    ? "チャットのリプレイがありません"
                    : "コメントを取得できませんでした";
                throw new CommentDownloadException(reason == null ? message : $"{message}\n{reason}");
            }

            return CommentParser.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

public sealed class CommentDownloadException(string message, Exception? inner = null) : Exception(message, inner);

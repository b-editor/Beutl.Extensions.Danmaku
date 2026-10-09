using System.Globalization;
using System.Text;

namespace Beutl.Extensions.Danmaku.Comments;

/// <summary>Writes comments in the text format <see cref="CommentParser.ParseText"/> reads.</summary>
public static class CommentWriter
{
    public static string ToText(IEnumerable<DanmakuComment> comments)
    {
        var sb = new StringBuilder();
        sb.Append("# 時刻\tコマンド\t本文\n");
        foreach (DanmakuComment comment in comments)
        {
            string text = comment.Text.Replace("\t", " ").Replace("\r\n", "\n").Replace("\n", "\\n");
            sb.Append(FormatTime(comment.Time)).Append('\t')
                .Append(CommentCommands.Format(comment)).Append('\t')
                .Append(text).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Formats a time as "m:ss.fff", or "h:mm:ss.fff" from an hour on.</summary>
    public static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            return "-" + FormatTime(-time);

        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}");
    }
}

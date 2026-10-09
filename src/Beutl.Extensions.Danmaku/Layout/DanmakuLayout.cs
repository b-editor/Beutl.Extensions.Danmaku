using Beutl.Extensions.Danmaku.Comments;

namespace Beutl.Extensions.Danmaku.Layout;

public sealed record DanmakuLayoutSettings(
    float Width,
    float Height,
    TimeSpan ScrollDuration,
    TimeSpan FixedDuration,
    float FontScale = 1,
    float LineHeight = 1.2f,
    bool ShowOverflow = true);

/// <summary>Where and when one comment is shown.</summary>
/// <param name="Index">The comment's index in the input list.</param>
/// <param name="Y">The top of the comment in pixels.</param>
/// <param name="Width">The width of the widest line in pixels.</param>
/// <param name="Height">The height of all lines in pixels.</param>
/// <param name="FontSize">The font size, already shrunk for fixed comments wider than the screen.</param>
/// <param name="Start">When the comment appears.</param>
/// <param name="End">When the comment disappears.</param>
/// <param name="IsOverflow">Whether no free row was found and the comment was placed at a random height.</param>
public readonly record struct PlacedComment(
    int Index,
    CommentPosition Position,
    float Y,
    float Width,
    float Height,
    float FontSize,
    TimeSpan Start,
    TimeSpan End,
    bool IsOverflow)
{
    /// <summary>The left edge at <paramref name="time"/>.</summary>
    public float GetX(TimeSpan time, float screenWidth)
    {
        if (Position != CommentPosition.Scroll)
            return (screenWidth - Width) / 2;

        double progress = (time - Start).TotalSeconds / (End - Start).TotalSeconds;
        return (float)(screenWidth - (screenWidth + Width) * progress);
    }
}

/// <summary>
/// Assigns each comment a height on screen the way video comment services do: a new comment takes the
/// highest row where it will not touch a comment already on screen, and a scrolling comment must neither
/// overlap the comment ahead of it when it enters nor catch up with it before that one leaves.
/// </summary>
public static class DanmakuLayout
{
    // Font sizes of a 384 px high player, the reference the comment format was designed for.
    private const float ReferenceHeight = 384f;

    public static float GetFontSize(CommentSize size, DanmakuLayoutSettings settings)
    {
        float reference = size switch
        {
            CommentSize.Big => 39f,
            CommentSize.Small => 15f,
            _ => 24f,
        };
        return reference * settings.Height / ReferenceHeight * settings.FontScale;
    }

    /// <summary>When a comment appears and disappears.</summary>
    /// <remarks>A scrolling comment enters a quarter of its pass before its time, as on video comment services.</remarks>
    public static (TimeSpan Start, TimeSpan End) GetTimeRange(DanmakuComment comment, DanmakuLayoutSettings settings)
    {
        TimeSpan scroll = settings.ScrollDuration > TimeSpan.Zero ? settings.ScrollDuration : TimeSpan.FromSeconds(4);
        TimeSpan fixedDuration = settings.FixedDuration > TimeSpan.Zero ? settings.FixedDuration : TimeSpan.FromSeconds(3);
        return comment.Position == CommentPosition.Scroll
            ? (comment.Time - scroll / 4, comment.Time - scroll / 4 + scroll)
            : (comment.Time, comment.Time + fixedDuration);
    }

    /// <param name="comments">Comments sorted by time.</param>
    /// <param name="settings">The screen and timing settings.</param>
    /// <param name="measureWidth">Returns the width of a comment's widest line at a font size.</param>
    /// <returns>Placements sorted by <see cref="PlacedComment.Start"/>.</returns>
    public static PlacedComment[] Arrange(
        IReadOnlyList<DanmakuComment> comments,
        DanmakuLayoutSettings settings,
        Func<DanmakuComment, float, float> measureWidth)
    {
        var placed = new List<PlacedComment>(comments.Count);
        var active = new Dictionary<CommentPosition, List<PlacedComment>>
        {
            [CommentPosition.Scroll] = [],
            [CommentPosition.Top] = [],
            [CommentPosition.Bottom] = [],
        };

        float width = settings.Width;
        float height = settings.Height;

        for (int index = 0; index < comments.Count; index++)
        {
            DanmakuComment comment = comments[index];
            float fontSize = GetFontSize(comment.Size, settings);
            float textWidth = measureWidth(comment, fontSize);
            if (comment.Position != CommentPosition.Scroll && textWidth > width && textWidth > 0)
            {
                // A fixed comment wider than the screen is shrunk to fit.
                fontSize *= width / textWidth;
                textWidth = measureWidth(comment, fontSize);
            }

            float textHeight = comment.Lines.Length * fontSize * settings.LineHeight;
            (TimeSpan start, TimeSpan end) = GetTimeRange(comment, settings);

            var candidate = new PlacedComment(index, comment.Position, 0, textWidth, textHeight, fontSize, start, end, false);
            List<PlacedComment> group = active[comment.Position];
            group.RemoveAll(other => other.End <= start);

            float? y = FindRow(candidate, group, width, height);
            if (y == null)
            {
                if (!settings.ShowOverflow)
                    continue;

                float range = Math.Max(0, height - textHeight);
                candidate = candidate with { Y = (float)(Random01(index) * range), IsOverflow = true };
            }
            else
            {
                candidate = candidate with { Y = y.Value };
                group.Add(candidate);
            }

            placed.Add(candidate);
        }

        placed.Sort((a, b) => a.Start.CompareTo(b.Start));
        return placed.ToArray();
    }

    private static float? FindRow(PlacedComment candidate, List<PlacedComment> active, float width, float height)
    {
        List<PlacedComment> conflicts = active.Where(other => Conflicts(other, candidate, width)).ToList();
        bool fromBottom = candidate.Position == CommentPosition.Bottom;

        // Rows start at a screen edge or right next to a comment that could be in the way.
        var rows = new List<float> { fromBottom ? height - candidate.Height : 0 };
        foreach (PlacedComment other in conflicts)
            rows.Add(fromBottom ? other.Y - candidate.Height : other.Y + other.Height);

        rows.Sort();
        if (fromBottom)
            rows.Reverse();

        foreach (float y in rows)
        {
            if (y < -0.01f || y + candidate.Height > height + 0.01f)
                continue;

            bool free = true;
            foreach (PlacedComment other in conflicts)
            {
                if (y < other.Y + other.Height && other.Y < y + candidate.Height)
                {
                    free = false;
                    break;
                }
            }

            if (free)
                return y;
        }

        return null;
    }

    /// <summary>Whether <paramref name="later"/> would touch <paramref name="earlier"/> if they shared a row.</summary>
    internal static bool Conflicts(PlacedComment earlier, PlacedComment later, float width)
    {
        if (earlier.End <= later.Start)
            return false;
        if (later.Position != CommentPosition.Scroll)
            return true;

        // The earlier comment must have fully entered the screen when the later one appears...
        if (earlier.GetX(later.Start, width) + earlier.Width > width)
            return true;

        // ...and the later one must not reach the left edge before the earlier one has left.
        return later.End > earlier.End && later.GetX(earlier.End, width) < 0;
    }

    private static double Random01(int index)
    {
        ulong z = unchecked((ulong)index + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}

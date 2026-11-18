using System.ComponentModel.DataAnnotations;
using Beutl.Composition;
using Beutl.Engine;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Layout;
using Beutl.Extensions.Danmaku.Properties;
using Beutl.Graphics;
using Beutl.Graphics.Rendering;
using Beutl.Media;
using Beutl.Media.TextFormatting;

namespace Beutl.Extensions.Danmaku.Graphics;

/// <summary>
/// Scrolls comments across the whole screen. The comments are stored in the object itself; the "コメント弾幕"
/// tool tab imports, downloads and edits them. Comment times are measured from the start of the object.
/// </summary>
/// <remarks>The original duration lasts until the last comment has left the screen.</remarks>
[Display(Name = nameof(Strings.Danmaku), ResourceType = typeof(Strings))]
public sealed partial class DanmakuDrawable : Drawable, IOriginalDurationProvider, ISplittable
{
    public DanmakuDrawable()
    {
        ScanProperties<DanmakuDrawable>();
    }

    [Display(Name = nameof(Strings.Comments), Description = nameof(Strings.CommentsDescription), ResourceType = typeof(Strings))]
    public IExpressionProperty<DanmakuCommentList> Comments { get; } = Property.CreateExpression(DanmakuCommentList.Empty);

    [Display(Name = nameof(Strings.Font), ResourceType = typeof(Strings))]
    public IExpressionProperty<FontFamily?> FontFamily { get; } = Property.CreateExpression<FontFamily?>(DefaultFont());

    [Range(10f, 400f)]
    [Display(Name = nameof(Strings.FontScale), ResourceType = typeof(Strings))]
    public IExpressionProperty<float> FontScale { get; } = Property.CreateExpression(100f);

    [Range(0.5f, 30f)]
    [Display(Name = nameof(Strings.ScrollDuration), ResourceType = typeof(Strings))]
    public IExpressionProperty<float> ScrollDuration { get; } = Property.CreateExpression(4f);

    [Range(0.5f, 30f)]
    [Display(Name = nameof(Strings.FixedDuration), ResourceType = typeof(Strings))]
    public IExpressionProperty<float> FixedDuration { get; } = Property.CreateExpression(3f);

    [Range(0f, 50f)]
    [Display(Name = nameof(Strings.OutlineWidth), Description = nameof(Strings.OutlineWidthDescription), ResourceType = typeof(Strings))]
    public IExpressionProperty<float> OutlineWidth { get; } = Property.CreateExpression(8f);

    [Display(Name = nameof(Strings.TimeOffset), Description = nameof(Strings.TimeOffsetDescription), ResourceType = typeof(Strings))]
    public IExpressionProperty<MediaTime> TimeOffset { get; } = Property.CreateExpression(MediaTime.Zero);

    [Display(Name = nameof(Strings.ShowOverflow), Description = nameof(Strings.ShowOverflowDescription), ResourceType = typeof(Strings))]
    public IExpressionProperty<bool> ShowOverflow { get; } = Property.CreateExpression(true);

    // The fonts of the host that loaded the extension, which new objects take their default font from; null outside
    // a host. The constructor has no composition context to read them from.
    internal static FontManager? HostFonts { get; set; }

    private static FontFamily DefaultFont()
    {
        var notoSansJp = new FontFamily("Noto Sans JP");
        return HostFonts?.IsRegistered(notoSansJp) == true ? notoSansJp : Media.FontFamily.Default;
    }

    public bool HasOriginalDuration()
    {
        return Comments.Value.Count > 0;
    }

    public bool TryGetOriginalDuration(out MediaTime timeSpan)
    {
        timeSpan = MediaTime.Zero;
        DanmakuCommentList comments = Comments.Value;
        if (comments.Count == 0)
            return false;

        // Only the timing settings matter here; the screen size does not move a comment's display time.
        var settings = new DanmakuLayoutSettings(
            1, 1, TimeSpan.FromSeconds(ScrollDuration.Value), TimeSpan.FromSeconds(FixedDuration.Value));
        TimeSpan lastEnd = comments.Items.Max(c => DanmakuLayout.GetTimeRange(c, settings).End);
        timeSpan = MediaTime.FromTimeSpan(lastEnd) + TimeOffset.Value;
        return timeSpan > MediaTime.Zero;
    }

    /// <summary>
    /// Keeps the comments where they were when the element is split. The back half starts later, so its comments move
    /// back by as much; both halves keep every comment, as both halves of a split video keep the whole file.
    /// </summary>
    public void NotifySplitted(bool backward, MediaTime startDelta, MediaTime durationDelta)
    {
        if (backward)
            TimeOffset.Value -= startDelta;
    }

    public partial class Resource
    {
        private readonly Dictionary<Color, Brush.Resource> _brushes = [];
        private readonly Dictionary<(Color, int), Pen.Resource> _pens = [];
        private DanmakuFrameLayout? _layout;
        private TimeSpan _commentTime;
        private FontManager? _fonts;

        protected override Size MeasureCore(Size availableSize)
        {
            return availableSize;
        }

        protected override void OnDraw(GraphicsContext2D context)
        {
            Size screen = context.Size;
            if (Comments is not { Count: > 0 } comments || screen.Width <= 0 || screen.Height <= 0)
                return;

            DanmakuFrameLayout layout = GetLayout(comments.Items, screen);
            TimeSpan time = _commentTime;
            PlacedComment[] placed = layout.Placed;

            // Placements are sorted by start; none lasts longer than the longest display time.
            int first = layout.FindFirstStartingAfter(time - layout.LongestDuration);
            float outlineWidth = Math.Max(0, OutlineWidth) / 100f;
            for (int i = first; i < placed.Length && placed[i].Start <= time; i++)
            {
                PlacedComment item = placed[i];
                if (time >= item.End)
                    continue;

                DanmakuComment comment = comments[item.Index];
                float x = item.GetX(time, screen.Width);
                float lineHeight = item.Height / comment.Lines.Length;
                Brush.Resource fill = GetBrush(comment.FillColor);
                Pen.Resource? pen = GetOutline(comment.FillColor, item.FontSize * outlineWidth);
                FormattedText[] lines = layout.GetLines(i);
                for (int line = 0; line < lines.Length; line++)
                {
                    FormattedText text = lines[line];
                    // The stroke outline is built from the text's own pen, not the one passed to DrawText.
                    text.Pen = pen;

                    // Centre the font's ascent-to-descent box in the row and draw from its baseline.
                    FontMetrics metrics = text.Metrics;
                    float box = metrics.Descent - metrics.Ascent;
                    float baseline = item.Y + line * lineHeight + (lineHeight - box) / 2 - metrics.Ascent;
                    using (context.PushTransform(Matrix.CreateTranslation(x, baseline)))
                    {
                        context.DrawText(text, fill, pen);
                    }
                }
            }
        }

        partial void PostReconcile(DanmakuDrawable obj, CompositionContext context)
        {
            bool changed = false;
            if (!ReferenceEquals(_fonts, context.Fonts))
            {
                _fonts = context.Fonts;
                changed = true;
            }

            TimeSpan time = (context.Time - obj.TimeRange.Value.Start - TimeOffset).ToTimeSpan();
            if (time != _commentTime)
            {
                _commentTime = time;
                changed = true;
            }

            if (changed)
                Version++;
        }

        partial void PostDispose(bool disposing)
        {
            _layout = null;
            foreach (Brush.Resource brush in _brushes.Values)
                brush.Dispose();
            _brushes.Clear();
            foreach (Pen.Resource pen in _pens.Values)
                pen.Dispose();
            _pens.Clear();
        }

        private DanmakuFrameLayout GetLayout(IReadOnlyList<DanmakuComment> comments, Size screen)
        {
            var settings = new DanmakuLayoutSettings(
                screen.Width,
                screen.Height,
                TimeSpan.FromSeconds(Math.Max(0.1, ScrollDuration)),
                TimeSpan.FromSeconds(Math.Max(0.1, FixedDuration)),
                Math.Max(0.01f, FontScale / 100f),
                ShowOverflow: ShowOverflow);
            FontManager fonts = _fonts ?? throw new InvalidOperationException("Text layout requires CompositionContext.Fonts.");
            FontFamily font = FontFamily ?? Media.FontFamily.Default;

            if (_layout == null || !_layout.Matches(fonts, comments, settings, font))
                _layout = new DanmakuFrameLayout(fonts, comments, settings, font);

            return _layout;
        }

        private Brush.Resource GetBrush(Color color)
        {
            if (!_brushes.TryGetValue(color, out Brush.Resource? brush))
            {
                brush = new SolidColorBrush(color).ToResource(CompositionContext.Default);
                _brushes[color] = brush;
            }

            return brush;
        }

        private Pen.Resource? GetOutline(Color fill, float thickness)
        {
            if (thickness <= 0.01f)
                return null;

            // Dark comments get a white outline so they stay readable.
            Color outline = 0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B < 60 ? Colors.White : Colors.Black;
            int key = (int)MathF.Round(thickness * 4);
            if (!_pens.TryGetValue((outline, key), out Pen.Resource? pen))
            {
                var penObject = new Pen();
                penObject.Brush.Value = new SolidColorBrush(outline);
                penObject.Thickness.Value = key / 4f;
                penObject.StrokeJoin.Value = StrokeJoin.Round;
                penObject.StrokeAlignment.Value = StrokeAlignment.Outside;
                pen = penObject.ToResource(CompositionContext.Default);
                _pens[(outline, key)] = pen;
            }

            return pen;
        }
    }
}

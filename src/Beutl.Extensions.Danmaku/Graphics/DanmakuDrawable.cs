using System.ComponentModel.DataAnnotations;
using Beutl.Composition;
using Beutl.Engine;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Layout;
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
[Display(Name = "コメント弾幕")]
public sealed partial class DanmakuDrawable : Drawable, IOriginalDurationProvider, ISplittable
{
    public DanmakuDrawable()
    {
        ScanProperties<DanmakuDrawable>();
    }

    [Display(Name = "コメント", Description = "ツールタブ「コメント弾幕」で取り込みや編集ができます")]
    public IExpressionProperty<DanmakuCommentList> Comments { get; } = Property.CreateExpression(DanmakuCommentList.Empty);

    [Display(Name = "フォント")]
    public IExpressionProperty<FontFamily?> FontFamily { get; } = Property.CreateExpression<FontFamily?>(DefaultFont());

    [Range(10f, 400f)]
    [Display(Name = "文字の大きさ (%)")]
    public IExpressionProperty<float> FontScale { get; } = Property.CreateExpression(100f);

    [Range(0.5f, 30f)]
    [Display(Name = "流れるコメントの表示時間 (秒)")]
    public IExpressionProperty<float> ScrollDuration { get; } = Property.CreateExpression(4f);

    [Range(0.5f, 30f)]
    [Display(Name = "固定コメントの表示時間 (秒)")]
    public IExpressionProperty<float> FixedDuration { get; } = Property.CreateExpression(3f);

    [Range(0f, 50f)]
    [Display(Name = "縁取りの太さ (%)", Description = "文字の大きさに対する縁取りの太さ")]
    public IExpressionProperty<float> OutlineWidth { get; } = Property.CreateExpression(8f);

    [Display(Name = "時刻のずれ", Description = "コメントの時刻にこの時間を足して表示します")]
    public IExpressionProperty<MediaTime> TimeOffset { get; } = Property.CreateExpression(MediaTime.Zero);

    [Display(Name = "あふれたコメントを表示", Description = "空いている行がないとき、ランダムな高さに重ねて表示します")]
    public IExpressionProperty<bool> ShowOverflow { get; } = Property.CreateExpression(true);

    private static FontFamily DefaultFont()
    {
        var notoSansJp = new FontFamily("Noto Sans JP");
        return FontManager.Instance.IsRegistered(notoSansJp) ? notoSansJp : Media.FontFamily.Default;
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

    protected override Size MeasureCore(Size availableSize, Drawable.Resource resource)
    {
        return availableSize;
    }

    protected override void OnDraw(GraphicsContext2D context, Drawable.Resource resource)
    {
        var r = (Resource)resource;
        Size screen = context.Size;
        if (r.Comments is not { Count: > 0 } comments || screen.Width <= 0 || screen.Height <= 0)
            return;

        DanmakuFrameLayout layout = r.GetLayout(comments.Items, screen);
        TimeSpan time = r.CommentTime;
        PlacedComment[] placed = layout.Placed;

        // Placements are sorted by start; none lasts longer than the longest display time.
        int first = layout.FindFirstStartingAfter(time - layout.LongestDuration);
        for (int i = first; i < placed.Length && placed[i].Start <= time; i++)
        {
            PlacedComment item = placed[i];
            if (time >= item.End)
                continue;

            DanmakuComment comment = comments[item.Index];
            float x = item.GetX(time, screen.Width);
            float lineHeight = item.Height / comment.Lines.Length;
            Brush.Resource fill = r.GetBrush(comment.FillColor);
            Pen.Resource? pen = r.GetOutline(comment.FillColor, item.FontSize * OutlineWidthFraction(r));
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

    private static float OutlineWidthFraction(Resource r) => Math.Max(0, r.OutlineWidth) / 100f;

    public partial class Resource
    {
        private readonly Dictionary<Color, Brush.Resource> _brushes = [];
        private readonly Dictionary<(Color, int), Pen.Resource> _pens = [];
        private DanmakuFrameLayout? _layout;

        internal TimeSpan CommentTime { get; private set; }

        partial void PostReconcile(DanmakuDrawable obj, CompositionContext context)
        {
            TimeSpan time = (context.Time - obj.TimeRange.Value.Start - TimeOffset).ToTimeSpan();
            if (time != CommentTime)
            {
                CommentTime = time;
                Version++;
            }
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

        internal DanmakuFrameLayout GetLayout(IReadOnlyList<DanmakuComment> comments, Size screen)
        {
            var settings = new DanmakuLayoutSettings(
                screen.Width,
                screen.Height,
                TimeSpan.FromSeconds(Math.Max(0.1, ScrollDuration)),
                TimeSpan.FromSeconds(Math.Max(0.1, FixedDuration)),
                Math.Max(0.01f, FontScale / 100f),
                ShowOverflow: ShowOverflow);
            FontFamily font = FontFamily ?? Media.FontFamily.Default;

            if (_layout == null || !_layout.Matches(comments, settings, font))
                _layout = new DanmakuFrameLayout(comments, settings, font);

            return _layout;
        }

        internal Brush.Resource GetBrush(Color color)
        {
            if (!_brushes.TryGetValue(color, out Brush.Resource? brush))
            {
                brush = new SolidColorBrush(color).ToResource(CompositionContext.Default);
                _brushes[color] = brush;
            }

            return brush;
        }

        internal Pen.Resource? GetOutline(Color fill, float thickness)
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

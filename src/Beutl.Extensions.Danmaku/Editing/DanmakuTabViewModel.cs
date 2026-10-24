using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Beutl.Editor;
using Beutl.Editor.Services;
using Beutl.Engine;
using Beutl.Extensibility;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Download;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Danmaku.Editing;

/// <summary>
/// The "コメント弾幕" tool tab: lists the comments of one danmaku object for editing, and imports, downloads or
/// exports them. The tab edits the object whose "編集" button opened it, not whatever the timeline selects.
/// Every change is one undoable edit of the object's comment list.
/// </summary>
public sealed class DanmakuTabViewModel : IToolContext
{
    public delegate Task<List<DanmakuComment>> DownloadFunc(string url, IProgress<string> progress, CancellationToken cancellationToken);

    private readonly IServiceProvider _services;
    private readonly DownloadFunc _download;
    private const string NoTargetMessage = "「編集」から開いてください";

    private const string MissingTargetMessage = "要素が削除されました";

    private readonly IEditorSelection? _selection;
    private readonly IEditorClock? _clock;
    private readonly HistoryManager? _history;
    private readonly List<CommentRowViewModel> _allRows = [];
    private CancellationTokenSource? _downloadCancellation;
    private bool _committing;

    // The object the tab was opened for, its element and scene. It is the target while it is in the scene.
    private DanmakuDrawable? _bound;
    private Element? _boundElement;
    private Scene? _scene;

    public DanmakuTabViewModel(ToolTabExtension extension, IServiceProvider services, DownloadFunc? download = null)
    {
        Extension = extension;
        _services = services;
        _download = download ?? ((url, progress, ct) => new CommentDownloader().DownloadAsync(url, progress, ct));
        _selection = services.GetService(typeof(IEditorSelection)) as IEditorSelection;
        _clock = services.GetService(typeof(IEditorClock)) as IEditorClock;
        _history = services.GetService(typeof(HistoryManager)) as HistoryManager;

        Filter.Subscribe(_ => ApplyFilter());
    }

    public static IReadOnlyList<string> PositionNames { get; } = ["流れる", "上", "下"];

    public static IReadOnlyList<string> SizeNames { get; } = ["普通", "大", "小"];

    public ToolTabExtension Extension { get; }

    public IReactiveProperty<bool> IsSelected { get; } = new ReactivePropertySlim<bool>();

    public IReadOnlyReactiveProperty<string> Header { get; } = new ReactivePropertySlim<string>("コメント弾幕");

    /// <summary>Whether the tab shows the import page instead of the editor.</summary>
    public ReactivePropertySlim<bool> IsImportPageOpen { get; } = new();

    /// <summary>A problem with what was entered on the import page, such as an unsupported URL; results go to the history.</summary>
    public ReactivePropertySlim<string?> ImportNotice { get; } = new();

    /// <summary>The imports and downloads into the edited object, newest first.</summary>
    public ObservableCollection<ImportJobViewModel> ImportJobs { get; } = [];

    /// <summary>Why nothing is being edited, or null while <see cref="Target"/> is set.</summary>
    public ReactivePropertySlim<string?> TargetMessage { get; } = new(NoTargetMessage);

    /// <summary>Which element is being edited, for the header.</summary>
    public ReactivePropertySlim<string> TargetDescription { get; } = new("");

    /// <summary>The element that holds <see cref="Target"/>.</summary>
    public ReactivePropertySlim<Element?> TargetElement { get; } = new();

    /// <summary>The danmaku object being edited, or null when there is none or it left the scene.</summary>
    public ReactivePropertySlim<DanmakuDrawable?> Target { get; } = new();

    /// <summary>The rows that match <see cref="Filter"/>.</summary>
    public ObservableCollection<CommentRowViewModel> Rows { get; } = [];

    public ReactivePropertySlim<string> Filter { get; } = new("");

    public ReactivePropertySlim<string> Summary { get; } = new("");

    public ReactivePropertySlim<string> Url { get; } = new("");

    /// <summary>Whether imports and downloads replace the comments instead of adding to them.</summary>
    public ReactivePropertySlim<bool> ReplaceOnImport { get; } = new(true);

    public ReactivePropertySlim<string?> Status { get; } = new();

    public ReactivePropertySlim<bool> IsDownloading { get; } = new();

    /// <summary>How many comments the target holds, regardless of the filter.</summary>
    public ReactivePropertySlim<int> TotalCount { get; } = new();

    /// <summary>What the empty list should say, or null while it shows rows.</summary>
    public ReactivePropertySlim<string?> EmptyMessage { get; } = new();

    /// <summary>
    /// Raised with the row now holding a comment that was just added or moved by a time edit, so the view can
    /// select it and scroll to it.
    /// </summary>
    public event Action<CommentRowViewModel>? RowFocusRequested;

    public object? GetService(Type serviceType) => _services.GetService(serviceType);

    /// <summary>Makes the tab edit <paramref name="drawable"/>, the object whose "編集" button was pressed.</summary>
    public void SetTarget(DanmakuDrawable? drawable)
    {
        if (ReferenceEquals(drawable, _bound))
            return;

        if (_scene != null)
            _scene.Children.CollectionChanged -= OnSceneChildrenChanged;

        // The history and a running download belong to the previous object.
        CancelDownload();
        ImportJobs.Clear();
        ImportNotice.Value = null;
        IsImportPageOpen.Value = false;

        _bound = drawable;
        _boundElement = drawable?.FindHierarchicalParent<Element>();
        _scene = _boundElement?.FindHierarchicalParent<Scene>();
        if (_scene != null)
            _scene.Children.CollectionChanged += OnSceneChildrenChanged;
        UpdatePresence();
    }

    public void OpenImportPage()
    {
        if (Target.Value != null)
            IsImportPageOpen.Value = true;
    }

    public void CloseImportPage() => IsImportPageOpen.Value = false;

    /// <summary>Selects the edited element on the timeline, to show which one the tab works on.</summary>
    public void SelectTargetInTimeline()
    {
        if (_selection != null && TargetElement.Value is { } element)
            _selection.Select(element);
    }

    public void AddAtCurrentTime()
    {
        if (Target.Value is not { } drawable)
            return;

        TimeSpan time = TimeSpan.Zero;
        if (_clock != null && TargetElement.Value is { } element)
            time = (_clock.CurrentTime.Value - element.Start.Value - drawable.TimeOffset.Value).ToTimeSpan();
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;

        var comment = new DanmakuComment(time, "コメント");
        Commit(drawable.Comments.Value.Add(comment), "コメントを追加");

        RequestFocus(comment);
    }

    /// <summary>Moves the playhead to the comment's time on the timeline.</summary>
    public void Seek(CommentRowViewModel row)
    {
        if (_clock != null && TargetElement.Value is { } element && Target.Value is { } drawable)
            _clock.Seek(element.Start.Value + drawable.TimeOffset.Value + MediaTime.FromTimeSpan(row.Comment.Time));
    }

    /// <summary>Sets the comment's time to the playhead.</summary>
    public void SetTimeToPlayhead(CommentRowViewModel row)
    {
        if (_clock != null && TargetElement.Value is { } element && Target.Value is { } drawable)
        {
            TimeSpan time = (_clock.CurrentTime.Value - element.Start.Value - drawable.TimeOffset.Value).ToTimeSpan();
            row.TimeText = CommentWriter.FormatTime(time < TimeSpan.Zero ? TimeSpan.Zero : time);
        }
    }

    public void Delete(IEnumerable<CommentRowViewModel> rows)
    {
        if (Target.Value is not { } drawable)
            return;

        int[] indices = rows.Select(r => r.Index).ToArray();
        if (indices.Length > 0)
            Commit(drawable.Comments.Value.RemoveAt(indices), "コメントを削除");
    }

    public void ClearAll()
    {
        if (Target.Value is { } drawable && drawable.Comments.Value.Count > 0)
            Commit(DanmakuCommentList.Empty, "コメントをすべて削除");
    }

    public void ImportFile(string path)
    {
        if (Target.Value == null)
        {
            Status.Value = "編集する対象がありません";
            return;
        }

        ImportNotice.Value = null;
        var job = new ImportJobViewModel(Path.GetFileName(path), "ファイル", "読み込み中…");
        ImportJobs.Insert(0, job);

        List<DanmakuComment> comments;
        try
        {
            comments = CommentFile.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException
                                       or System.Text.Json.JsonException)
        {
            Fail(job, $"読み込めませんでした: {ex.Message}");
            return;
        }

        if (comments.Count == 0)
        {
            Fail(job, "コメントが見つかりませんでした");
            return;
        }

        ApplyImported(comments, "コメントを取り込み");
        Succeed(job, comments.Count);
    }

    public void ExportFile(string path)
    {
        if (Target.Value is not { } drawable)
            return;

        try
        {
            CommentFile.Write(path, drawable.Comments.Value.Items);
            Status.Value = $"{drawable.Comments.Value.Count:N0} 件を書き出しました";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Value = $"書き出せませんでした: {ex.Message}";
        }
    }

    /// <summary>Downloads the comments of <see cref="Url"/> into the edited object.</summary>
    public async Task DownloadAsync()
    {
        if (IsDownloading.Value)
            return;
        if (Target.Value is not { } target)
        {
            Status.Value = "編集する対象がありません";
            return;
        }

        string url = Url.Value.Trim();
        if (CommentSourceDetector.Detect(url) is not { } source)
        {
            Status.Value = ImportNotice.Value = "対応していない URL です";
            return;
        }

        ImportNotice.Value = null;
        _downloadCancellation = new CancellationTokenSource();
        IsDownloading.Value = true;
        Status.Value = null;
        var job = new ImportJobViewModel(url, source.SiteName, "取得中…", CancelDownload);
        ImportJobs.Insert(0, job);
        try
        {
            var progress = new Progress<string>(message => job.Message = message);
            List<DanmakuComment> comments = await _download(url, progress, _downloadCancellation.Token);
            if (comments.Count == 0)
            {
                Fail(job, "コメントがありませんでした");
                return;
            }

            if (!ReferenceEquals(Target.Value, target))
            {
                Fail(job, "対象が変わったため反映していません");
                return;
            }

            ApplyImported(comments, "コメントをダウンロード");
            Succeed(job, comments.Count);
        }
        catch (OperationCanceledException)
        {
            job.Finish(ImportJobState.Canceled, "中止しました");
            Status.Value = "中止しました";
        }
        catch (CommentDownloadException ex)
        {
            Fail(job, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.ComponentModel.Win32Exception)
        {
            Fail(job, $"取得できませんでした: {ex.Message}");
        }
        finally
        {
            IsDownloading.Value = false;
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
        }
    }

    public void CancelDownload()
    {
        _downloadCancellation?.Cancel();
    }

    // The result goes to the history entry, and to the status line the editor shows.
    private void Succeed(ImportJobViewModel job, int count)
    {
        string message = string.Create(CultureInfo.InvariantCulture, $"{count:N0} 件を{(ReplaceOnImport.Value ? "取り込みました" : "追加しました")}");
        job.Finish(ImportJobState.Succeeded, message);
        Status.Value = message;
    }

    private void Fail(ImportJobViewModel job, string message)
    {
        job.Finish(ImportJobState.Failed, message);
        Status.Value = message;
    }

    internal void OnRowEdited(CommentRowViewModel row)
    {
        if (_committing || Target.Value is not { } drawable)
            return;

        DanmakuCommentList list = drawable.Comments.Value;
        if (row.Index >= list.Count)
            return;

        if (row.TryBuild() is not { } comment)
        {
            Status.Value = "読み取れない値を元に戻しました";
            row.Load(row.Index, list[row.Index]);
            return;
        }

        if (comment != list[row.Index])
        {
            Commit(list.Replace(row.Index, comment), "コメントを編集");
            // A time edit can move the comment to another row; let the view follow it.
            RequestFocus(comment);
        }
    }

    private void RequestFocus(DanmakuComment comment)
    {
        if (_allRows.FirstOrDefault(r => ReferenceEquals(r.Comment, comment)) is { } row)
            RowFocusRequested?.Invoke(row);
    }

    private void ApplyImported(IReadOnlyCollection<DanmakuComment> comments, string commandName)
    {
        if (Target.Value is not { } drawable)
            return;

        DanmakuCommentList current = drawable.Comments.Value;
        DanmakuCommentList next = ReplaceOnImport.Value ? new DanmakuCommentList(comments) : current.AddRange(comments);
        if (!next.Equals(current))
            Commit(next, commandName);
    }

    private void Commit(DanmakuCommentList list, string commandName)
    {
        if (Target.Value is not { } drawable)
            return;

        _committing = true;
        try
        {
            drawable.Comments.Value = list;
            _history?.Commit(commandName);
        }
        finally
        {
            _committing = false;
        }

        SyncRows();
    }

    private void OnSceneChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdatePresence();

    // Deleting the element hides the editor; undoing the deletion brings it back.
    private void UpdatePresence()
    {
        bool present = _bound != null && _boundElement != null && _scene != null
                       && _scene.Children.Contains(_boundElement)
                       && ReferenceEquals(_bound.FindHierarchicalParent<Element>(), _boundElement);
        ApplyTarget(present ? _bound : null, present ? _boundElement : null);
        TargetMessage.Value = present ? null : _bound == null ? NoTargetMessage : MissingTargetMessage;
        if (!present)
            IsImportPageOpen.Value = false;
    }

    private void ApplyTarget(DanmakuDrawable? drawable, Element? element)
    {
        if (!ReferenceEquals(drawable, Target.Value))
        {
            if (Target.Value is { } previous)
                previous.Comments.ValueChanged -= OnCommentsChanged;

            TargetElement.Value = element;
            Target.Value = drawable;
            if (drawable != null)
                drawable.Comments.ValueChanged += OnCommentsChanged;

            _allRows.Clear();
            SyncRows();
        }

        TargetDescription.Value = element == null ? ""
            : !string.IsNullOrWhiteSpace(element.Name.Value) ? $"要素「{element.Name.Value}」"
            : $"{CommentWriter.FormatTime(element.Start.Value.ToTimeSpan())} から始まる要素";
    }

    private void OnCommentsChanged(object? sender, PropertyValueChangedEventArgs<DanmakuCommentList> e)
    {
        // Undo and redo change the list from outside the tab.
        if (Dispatcher.UIThread.CheckAccess())
            SyncRows();
        else
            Dispatcher.UIThread.Post(SyncRows);
    }

    private void SyncRows()
    {
        DanmakuCommentList list = Target.Value?.Comments.Value ?? DanmakuCommentList.Empty;
        if (list.Count == _allRows.Count)
        {
            // Update in place so the table keeps its scroll position and focus.
            for (int i = 0; i < list.Count; i++)
            {
                if (!ReferenceEquals(_allRows[i].Comment, list[i]))
                    _allRows[i].Load(i, list[i]);
            }
        }
        else
        {
            _allRows.Clear();
            for (int i = 0; i < list.Count; i++)
                _allRows.Add(new CommentRowViewModel(this, i, list[i]));
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string filter = Filter.Value.Trim();
        List<CommentRowViewModel> visible = filter.Length == 0
            ? _allRows
            : _allRows.Where(r => r.Comment.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!visible.SequenceEqual(Rows))
        {
            Rows.Clear();
            foreach (CommentRowViewModel row in visible)
                Rows.Add(row);
        }

        Summary.Value = Target.Value == null
            ? ""
            : filter.Length == 0 ? $"{_allRows.Count} 件" : $"{visible.Count} / {_allRows.Count} 件";
        TotalCount.Value = _allRows.Count;
        EmptyMessage.Value = Target.Value == null || visible.Count > 0 ? null
            : _allRows.Count == 0 ? "コメントがありません"
            : "一致するコメントはありません";
    }

    public void WriteToJson(JsonObject json)
    {
        json[nameof(ReplaceOnImport)] = ReplaceOnImport.Value;
        // A tab restored with the layout finds its object again by id.
        if (_bound != null)
            json["Target"] = _bound.Id.Value.ToString();
    }

    public void ReadFromJson(JsonObject json)
    {
        if (json.TryGetPropertyValue(nameof(ReplaceOnImport), out JsonNode? node) && node is JsonValue value
                                                                                  && value.TryGetValue(out bool replace))
        {
            ReplaceOnImport.Value = replace;
        }

        if (json.TryGetPropertyValue("Target", out JsonNode? target) && target is JsonValue targetValue
            && targetValue.TryGetValue(out string? text) && Guid.TryParse(text, out Guid id)
            && _services.GetService(typeof(Scene)) is Scene scene)
        {
            SetTarget(Find(scene, id));
        }

        static DanmakuDrawable? Find(IHierarchical node, Guid id)
        {
            foreach (IHierarchical child in node.HierarchicalChildren)
            {
                if (child is DanmakuDrawable drawable && drawable.Id.Value == id)
                    return drawable;
                if (Find(child, id) is { } found)
                    return found;
            }

            return null;
        }
    }

    public void Dispose()
    {
        if (_scene != null)
            _scene.Children.CollectionChanged -= OnSceneChildrenChanged;
        if (Target.Value is { } drawable)
            drawable.Comments.ValueChanged -= OnCommentsChanged;
        _downloadCancellation?.Cancel();
    }
}

using Beutl.Collections;
using Beutl.Editor;
using Beutl.Editor.Observers;
using Beutl.Editor.Services;
using Beutl.Extensibility;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Editing;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.ProjectSystem;
using Reactive.Bindings;

namespace Beutl.Extensions.Danmaku.Tests;

/// <summary>A scene with one danmaku element, the editor services the tool tab reads, and the tab itself.</summary>
internal sealed class EditorFixture : IDisposable
{
    private readonly Dictionary<Type, object> _services = [];
    private readonly CoreObjectOperationObserver _observer;
    private readonly IDisposable _historySubscription;

    public EditorFixture(string comments = "1.0\t\tいち\n2.0\tred\t赤いコメント\n3.0\tue\tさん", DanmakuTabViewModel.DownloadFunc? download = null)
    {
        Scene = new Scene(1920, 1080, "Fixture");
        Drawable = new DanmakuDrawable();
        Drawable.Comments.Value = new DanmakuCommentList(CommentParser.Parse(comments));
        Element = new Element
        {
            Start = { Value = MediaTime.FromSeconds(1) },
            Length = { Value = MediaTime.FromSeconds(10) },
            ZIndex = { Value = 2 },
        };
        Element.AddObject(Drawable);
        Scene.AddChild(Element);

        // Wired like the editor's EditViewModel: the observer reports changes in the scene to the history.
        var sequence = new OperationSequenceGenerator();
        _observer = new CoreObjectOperationObserver(null, Scene, sequence);
        History = new HistoryManager(sequence);
        _historySubscription = History.Subscribe(_observer);
        _services[typeof(IEditorSelection)] = Selection;
        _services[typeof(IEditorClock)] = Clock;
        _services[typeof(HistoryManager)] = History;
        _services[typeof(Scene)] = Scene;
        Context = new FakeEditorContext(Scene, this);
        ViewModel = new DanmakuTabViewModel(new DanmakuTabExtension(), Context, download);
    }

    public Scene Scene { get; }

    public Element Element { get; }

    public DanmakuDrawable Drawable { get; }

    public HistoryManager History { get; }

    public FakeSelection Selection { get; } = new();

    public FakeClock Clock { get; } = new();

    public FakeEditorContext Context { get; }

    public DanmakuTabViewModel ViewModel { get; }

    /// <summary>Adds another danmaku element to the scene, as a second object the tab can be opened for.</summary>
    public DanmakuDrawable AddDanmaku(string comments, TimeSpan start)
    {
        var drawable = new DanmakuDrawable();
        drawable.Comments.Value = new DanmakuCommentList(CommentParser.Parse(comments));
        var element = new Element
        {
            Start = { Value = MediaTime.FromTimeSpan(start) },
            Length = { Value = MediaTime.FromSeconds(10) },
            ZIndex = { Value = 5 },
        };
        element.AddObject(drawable);
        Scene.AddChild(element);
        return drawable;
    }

    public string[] Texts => Drawable.Comments.Value.Items.Select(c => c.Text).ToArray();

    public object? GetService(Type type) => _services.GetValueOrDefault(type);

    public void Dispose()
    {
        ViewModel.Dispose();
        _historySubscription.Dispose();
        History.Dispose();
        _observer.Dispose();
    }
}

internal sealed class FakeSelection : IEditorSelection
{
    private readonly ReactivePropertySlim<CoreObject?> _selectedObject = new();

    public IReadOnlyReactiveProperty<CoreObject?> SelectedObject => _selectedObject;

    public IReadOnlyReactiveProperty<int?> SelectedLayerNumber { get; } = new ReactivePropertySlim<int?>();

    public ICoreList<Element> SelectedElements { get; } = new CoreList<Element>();

    public void Select(CoreObject? obj, bool notifyIfSelected = false)
    {
        _selectedObject.Value = obj;
        SelectedElements.Clear();
        if (obj is Element element)
            SelectedElements.Add(element);
    }
}

internal sealed class FakeClock : IEditorClock
{
    private readonly ReactivePropertySlim<MediaTime> _currentTime = new();

    public IReadOnlyReactiveProperty<MediaTime> CurrentTime => _currentTime;

    public IReadOnlyReactiveProperty<MediaTime> MaximumTime { get; } = new ReactivePropertySlim<MediaTime>(MediaTime.FromSeconds(300));

    public void Seek(MediaTime time) => _currentTime.Value = time;
}

internal sealed class FakeEditorContext(CoreObject obj, EditorFixture fixture) : IEditorContext
{
    public CoreObject Object => obj;

    public EditorExtension Extension => throw new NotSupportedException();

    public IReactiveProperty<bool> IsEnabled { get; } = new ReactivePropertySlim<bool>(true);

    public object? GetService(Type serviceType) => fixture.GetService(serviceType);

    /// <summary>Every OpenToolTab call, in order; the open tabs are the distinct ones.</summary>
    public List<IToolContext> OpenRequests { get; } = [];

    public T? FindToolTab<T>(Func<T, bool> condition)
        where T : IToolContext => OpenRequests.OfType<T>().FirstOrDefault(condition);

    public T? FindToolTab<T>()
        where T : IToolContext => OpenRequests.OfType<T>().FirstOrDefault();

    public bool OpenToolTab(IToolContext item)
    {
        OpenRequests.Add(item);
        return true;
    }

    public void CloseToolTab(IToolContext item)
    {
        OpenRequests.RemoveAll(t => ReferenceEquals(t, item));
    }
}

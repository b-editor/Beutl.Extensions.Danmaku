using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Extensibility;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Editing;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.PropertyAdapters;

namespace Beutl.Extensions.Danmaku.Tests;

// Drives the tool tab view and the property panel's summary in a headless Avalonia session.
public class DanmakuTabViewTests
{
    private static Window Show(Control content)
    {
        var window = new Window { Width = 900, Height = 700, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T[] Find<T>(Control root)
        where T : Control => root.GetVisualDescendants().OfType<T>().Where(c => c.IsEffectivelyVisible).ToArray();

    [AvaloniaTest]
    public void TabExtensionCreatesTheViewAndItsContext()
    {
        using var f = new EditorFixture();
        var extension = new DanmakuTabExtension();

        Assert.That(extension.TryCreateContent(f.Context, out Control? control), Is.True);
        Assert.That(extension.TryCreateContext(f.Context, out IToolContext? context), Is.True);
        Assert.That(control, Is.TypeOf<DanmakuTabView>());
        Assert.That(context, Is.TypeOf<DanmakuTabViewModel>());
        Assert.That(context!.Header.Value, Is.EqualTo("コメント弾幕"));
        Assert.That(extension.Header, Is.Null, "Not in the add-tab menu; the tab opens from an object's 編集 button.");
        context.Dispose();
    }

    private static (Window Window, DanmakuTabView View) Open(EditorFixture f, bool select = true, double width = 1200)
    {
        var view = new DanmakuTabView { DataContext = f.ViewModel };
        var window = new Window { Width = width, Height = 720, Content = view };
        window.Show();
        if (select)
            f.ViewModel.SetTarget(f.Drawable);
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static ListBox List(Control view) => Find<ListBox>(view).Single();

    private static void Select(Control view, params CommentRowViewModel[] rows)
    {
        ListBox list = List(view);
        if (rows.Length == 1)
        {
            // Like a click: the selection moves straight to the row, without passing through "nothing selected".
            list.SelectedItem = rows[0];
        }
        else
        {
            list.SelectedItems!.Clear();
            foreach (CommentRowViewModel row in rows)
                list.SelectedItems.Add(row);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static TextBox Editor(Control view, string text) => Find<TextBox>(view).Single(t => t.Text == text);

    // Icon buttons carry their name as the tooltip, like Beutl's own tool bars.
    private static Button ByTip(Control view, string name) =>
        Find<Button>(view).Single(b => ToolTip.GetTip(b) is string tip && tip.StartsWith(name, StringComparison.Ordinal));

    private static void Click(Button button)
    {
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static ComboBox Choice(Control view, string item) =>
        Find<ComboBox>(view).Single(c => c.ItemsSource is IEnumerable<string> items && items.Contains(item));

    private static ScrollViewer[] DetailPane(Control view) =>
        Find<ScrollViewer>(view).Where(s => s.Content is Border b && b.Classes.Contains("options-group")).ToArray();

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, key == Key.Enter ? PhysicalKey.Enter : PhysicalKey.Delete, null);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaTest]
    public void ExplainsWhatToDoWithoutATarget()
    {
        using var f = new EditorFixture();
        (_, DanmakuTabView view) = Open(f, select: false);

        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Does.Contain("「編集」から開いてください"));
        Assert.That(Find<ListBox>(view), Is.Empty);
        Assert.That(Find<Button>(view).Where(b => ToolTip.GetTip(b) as string == "コメントを取り込む"), Is.Empty, "There is nothing to import into.");

        f.ViewModel.SetTarget(f.Drawable);
        Dispatcher.UIThread.RunJobs();

        Assert.That(Find<ListBoxItem>(view), Has.Length.EqualTo(3));
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Is.SupersetOf(new[] { "コメント弾幕", "3 件", "いち", "赤いコメント", "0:02.000" }));
        // The locate button says which element the tab edits.
        Assert.That(ToolTip.GetTip(ByTip(view, "タイムラインで選択")), Is.EqualTo("タイムラインで選択（0:01.000 から始まる要素）"));
        // The list only shows comments; nothing is editable until one is selected.
        Assert.That(Find<TextBox>(view).Select(t => t.Text), Does.Not.Contain("いち"));

        Click(ByTip(view, "タイムラインで選択"));
        Assert.That(f.Selection.SelectedObject.Value, Is.SameAs(f.Element));
    }

    [AvaloniaTest]
    public void TheImportPageTakesTheWholeTabAndGoesBack()
    {
        using var f = new EditorFixture("");
        (Window window, DanmakuTabView view) = Open(f);

        Click(ByTip(view, "コメントを取り込む"));

        // Like a browser's download page: the editor is gone, the ways to import and the history take the tab.
        Assert.That(Find<ListBox>(view), Is.Empty);
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Is.SupersetOf(new[] { "コメントを取り込む", "URL", "ファイル", "既存のコメント" }));
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Does.Not.Contain("履歴"), "no history until something is imported");

        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"import-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "1.0\t\tとりこみ");
        f.ViewModel.ImportFile(path);
        Dispatcher.UIThread.RunJobs();
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Is.SupersetOf(new[] { "履歴", "完了", Path.GetFileName(path), "1 件を取り込みました" }));

        Click(ByTip(view, "戻る"));
        Assert.That(Find<ListBoxItem>(view), Has.Length.EqualTo(1));
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Does.Contain("1 件を取り込みました"), "The result is shown on the editor too.");

        // Esc also goes back.
        f.ViewModel.OpenImportPage();
        Dispatcher.UIThread.RunJobs();
        Find<Button>(view).Single(b => b.Content as string == "ファイルを選ぶ…").Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.That(f.ViewModel.IsImportPageOpen.Value, Is.False);
    }

    [AvaloniaTest]
    public void ARunningDownloadShowsProgressAndCanBeCanceledFromTheHistory()
    {
        using var f = new EditorFixture(download: async (_, progress, ct) =>
        {
            progress.Report("123 件");
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        (_, DanmakuTabView view) = Open(f);
        f.ViewModel.OpenImportPage();
        f.ViewModel.Url.Value = "https://www.twitch.tv/videos/123";

        Task download = f.ViewModel.DownloadAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.That(Find<ProgressBar>(view), Has.Length.EqualTo(1));
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Is.SupersetOf(new[] { "https://www.twitch.tv/videos/123", "123 件", "取得中" }));

        Click(ByTip(view, "中止"));

        Assert.That(download.IsCompleted, Is.True);
        Assert.That(f.ViewModel.ImportJobs.Single().State, Is.EqualTo(ImportJobState.Canceled));
        Assert.That(Find<ProgressBar>(view), Is.Empty);
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Is.SupersetOf(new[] { "中止", "中止しました" }));
    }

    [AvaloniaTest]
    public void AShortNarrowTabKeepsTheListAndScrollsTheEditor()
    {
        using var f = new EditorFixture();
        var view = new DanmakuTabView { DataContext = f.ViewModel };
        // Short enough that the editor cannot fit below the list.
        var window = new Window { Width = 560, Height = 480, Content = view };
        window.Show();
        f.ViewModel.SetTarget(f.Drawable);
        Dispatcher.UIThread.RunJobs();
        Select(view, f.ViewModel.Rows[0]);

        // Stacked, the list keeps a usable height and the editor scrolls instead of being cut off.
        Assert.That(List(view).Bounds.Height, Is.GreaterThanOrEqualTo(100));
        ScrollViewer editor = DetailPane(view).Single();
        Assert.That(editor.Extent.Height, Is.GreaterThan(editor.Viewport.Height));
        Assert.That(editor.Bounds.Bottom, Is.LessThanOrEqualTo(view.Bounds.Height));
    }

    [AvaloniaTest]
    public void SelectingARowShowsItInTheEditor()
    {
        using var f = new EditorFixture();
        (_, DanmakuTabView view) = Open(f);

        Select(view, f.ViewModel.Rows[1]);

        Assert.That(Find<TextBox>(view).Select(t => t.Text), Is.SupersetOf(new[] { "0:02.000", "#FF0000", "赤いコメント" }));
        Assert.That(Choice(view, "流れる").SelectedItem, Is.EqualTo("流れる"));
        Assert.That(Choice(view, "普通").SelectedItem, Is.EqualTo("普通"));

        Select(view, f.ViewModel.Rows[2]);
        Assert.That(Choice(view, "流れる").SelectedItem, Is.EqualTo("上"));
    }

    [AvaloniaTest]
    public void EnterCommitsAnEdit()
    {
        using var f = new EditorFixture();
        (Window window, DanmakuTabView view) = Open(f);
        Select(view, f.ViewModel.Rows[0]);

        TextBox box = Editor(view, "いち");
        box.Focus();
        box.Text = "にゅうりょく";
        Assert.That(f.Texts[0], Is.EqualTo("いち"), "The edit waits for Enter or focus loss.");

        Press(window, Key.Enter);

        Assert.That(f.Texts[0], Is.EqualTo("にゅうりょく"));
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Does.Contain("にゅうりょく"), "The list shows the edit.");
    }

    [AvaloniaTest]
    public void ShiftEnterBreaksTheLineInTheText()
    {
        using var f = new EditorFixture();
        (Window window, DanmakuTabView view) = Open(f);
        Select(view, f.ViewModel.Rows[0]);

        TextBox box = Editor(view, "いち");
        box.Focus();
        box.CaretIndex = 1;
        Press(window, Key.Enter, RawInputModifiers.Shift);
        Assert.That(box.Text, Is.EqualTo("い\nち"));
        Assert.That(f.Texts[0], Is.EqualTo("いち"));

        Press(window, Key.Enter);
        Assert.That(f.Texts[0], Is.EqualTo("い\nち"));
        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Does.Contain("い ↵ ち"));
    }

    [AvaloniaTest]
    public void SwitchingTheSelectionSavesWhatWasTyped()
    {
        using var f = new EditorFixture();
        (_, DanmakuTabView view) = Open(f);
        Select(view, f.ViewModel.Rows[0]);

        TextBox box = Editor(view, "いち");
        box.Focus();
        box.Text = "入力中";
        Select(view, f.ViewModel.Rows[2]);

        // The typed text went to the comment it was typed for, not to the newly selected one.
        Assert.That(f.Texts, Is.EqualTo(new[] { "入力中", "赤いコメント", "さん" }));
        Assert.That(Find<TextBox>(view).Select(t => t.Text), Does.Contain("さん"));
    }

    [AvaloniaTest]
    public void ATimeEditKeepsTheEditedCommentSelected()
    {
        using var f = new EditorFixture();
        (Window window, DanmakuTabView view) = Open(f);
        Select(view, f.ViewModel.Rows[0]);

        TextBox time = Editor(view, "0:01.000");
        time.Focus();
        time.Text = "0:05";
        Press(window, Key.Enter);

        // "いち" is now the last comment, and the editor still shows it.
        Assert.That(f.Texts, Is.EqualTo(new[] { "赤いコメント", "さん", "いち" }));
        var selected = List(view).SelectedItems!.Cast<CommentRowViewModel>().Single();
        Assert.That(selected.Text, Is.EqualTo("いち"));
        Assert.That(Find<TextBox>(view).Select(t => t.Text), Is.SupersetOf(new[] { "0:05.000", "いち" }));
    }

    [AvaloniaTest]
    public void PaletteAndChoicesEditTheStyle()
    {
        using var f = new EditorFixture();
        (_, DanmakuTabView view) = Open(f);
        Select(view, f.ViewModel.Rows[0]);

        Find<Button>(view).Single(b => ToolTip.GetTip(b) as string == "青").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Choice(view, "流れる").SelectedItem = "下";
        Choice(view, "普通").SelectedItem = "小";
        Dispatcher.UIThread.RunJobs();

        DanmakuComment comment = f.Drawable.Comments.CurrentValue.Items.Single(c => c.Text == "いち");
        Assert.That(comment.Color, Is.EqualTo(Beutl.Media.Color.FromArgb(255, 0, 0, 255)));
        Assert.That(comment.Position, Is.EqualTo(CommentPosition.Bottom));
        Assert.That(comment.Size, Is.EqualTo(CommentSize.Small));

        // White is the default, so picking it clears the colour.
        Find<Button>(view).Single(b => ToolTip.GetTip(b) as string == "白").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.That(f.Drawable.Comments.CurrentValue.Items.Single(c => c.Text == "いち").Color, Is.Null);
    }

    [AvaloniaTest]
    public void SeveralSelectedRowsCanBeDeletedTogether()
    {
        using var f = new EditorFixture();
        (_, DanmakuTabView view) = Open(f);
        Select(view, f.ViewModel.Rows[0], f.ViewModel.Rows[2]);

        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Does.Contain("2 件を選択中"));
        Click(Find<Button>(view).Single(b => b.Content as string == "削除"));

        Assert.That(f.Texts, Is.EqualTo(new[] { "赤いコメント" }));
    }

    [AvaloniaTest]
    public void DeleteKeyRemovesTheSelectedRows()
    {
        using var f = new EditorFixture();
        (Window window, DanmakuTabView view) = Open(f);
        Select(view, f.ViewModel.Rows[0], f.ViewModel.Rows[2]);

        // The rows take focus, not the list itself.
        Assert.That(List(view).ContainerFromIndex(2)!.Focus(), Is.True);
        Press(window, Key.Delete);

        Assert.That(f.Texts, Is.EqualTo(new[] { "赤いコメント" }));
    }

    [AvaloniaTest]
    public void DeleteInsideTheEditorEditsTheTextInstead()
    {
        using var f = new EditorFixture();
        (Window window, DanmakuTabView view) = Open(f);
        Select(view, f.ViewModel.Rows[0]);

        TextBox box = Editor(view, "いち");
        box.Focus();
        box.CaretIndex = 0;
        Press(window, Key.Delete);

        Assert.That(box.Text, Is.EqualTo("ち"));
        Assert.That(f.Texts, Has.Length.EqualTo(3));
    }

    [AvaloniaTest]
    public void ClickingARowSelectsIt()
    {
        using var f = new EditorFixture();
        (Window window, DanmakuTabView view) = Open(f);

        TextBlock cell = List(view).ContainerFromIndex(1)!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "赤いコメント");
        Point center = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.That(List(view).SelectedItems!.Cast<CommentRowViewModel>(), Is.EqualTo(new[] { f.ViewModel.Rows[1] }));
        Assert.That(Find<TextBox>(view).Select(t => t.Text), Does.Contain("赤いコメント"));
    }

    [AvaloniaTest]
    public void ImportModeIsChosenWithAComboBox()
    {
        using var f = new EditorFixture("");
        (_, DanmakuTabView view) = Open(f);
        f.ViewModel.OpenImportPage();
        Dispatcher.UIThread.RunJobs();

        Assert.That(Choice(view, "置き換える").SelectedItem, Is.EqualTo("置き換える"));
        Choice(view, "置き換える").SelectedItem = "追加する";
        Assert.That(f.ViewModel.ReplaceOnImport.Value, Is.False);
        Choice(view, "置き換える").SelectedItem = "置き換える";
        Assert.That(f.ViewModel.ReplaceOnImport.Value, Is.True);
    }

    [AvaloniaTest]
    public void AnEmptyListHidesTheEditorAndOffersTheImport()
    {
        using var f = new EditorFixture("");
        (_, DanmakuTabView view) = Open(f);

        Assert.That(Find<TextBlock>(view).Select(t => t.Text), Does.Contain("コメントがありません"));
        Assert.That(DetailPane(view), Is.Empty);
        // The import is offered both in the tool bar and in the empty list.
        Assert.That(ByTip(view, "コメントを取り込む"), Is.Not.Null);
        Click(Find<Button>(view).Single(b => b.Content as string == "取り込む…"));
        Assert.That(f.ViewModel.IsImportPageOpen.Value, Is.True);
        f.ViewModel.CloseImportPage();

        f.ViewModel.AddAtCurrentTime();
        Dispatcher.UIThread.RunJobs();
        Assert.That(DetailPane(view), Has.Length.EqualTo(1));
    }

    [AvaloniaTest]
    [TestCase(1200, true)]
    [TestCase(500, false)]
    public void TheEditorSitsBesideTheListWhenThereIsRoom(double width, bool beside)
    {
        using var f = new EditorFixture();
        (Window window, DanmakuTabView view) = Open(f, width: width);
        Select(view, f.ViewModel.Rows[0]);

        ListBox list = List(view);
        ScrollViewer title = DetailPane(view).Single();
        Point listOrigin = list.TranslatePoint(default, window)!.Value;
        Point titleOrigin = title.TranslatePoint(default, window)!.Value;

        if (beside)
            Assert.That(titleOrigin.X, Is.GreaterThan(listOrigin.X + list.Bounds.Width));
        else
            Assert.That(titleOrigin.Y, Is.GreaterThan(listOrigin.Y + list.Bounds.Height));
    }

    private static (IPropertyEditorContext Context, Control Control) CreateCommentEditor(DanmakuDrawable drawable, IServiceProvider? services)
    {
        var extension = new CommentListPropertyEditorExtension();
        List<IPropertyAdapter> adapters = drawable.Properties.Select(p => (IPropertyAdapter)Activator.CreateInstance(
            typeof(EnginePropertyAdapter<>).MakeGenericType(p.ValueType), p, drawable)!).ToList();

        IPropertyAdapter[] claimed = adapters.Where(a => extension.MatchProperty([a]).Any()).ToArray();
        Assert.That(claimed.Select(a => a.GetEngineProperty()!.Name), Is.EqualTo(new[] { nameof(DanmakuDrawable.Comments) }));
        Assert.That(extension.TryCreateContext(claimed, out IPropertyEditorContext? context), Is.True);
        // The host visits each context with its property panel, which resolves the editor's services.
        if (services != null)
            context!.Accept(new Visitor(services));
        Assert.That(extension.TryCreateControl(context!, out Control? control), Is.True);
        return (context!, control!);
    }

    [AvaloniaTest]
    public void PropertyPanelShowsTheCommentCount()
    {
        var drawable = new DanmakuDrawable();
        drawable.Comments.CurrentValue = new DanmakuCommentList(CommentParser.Parse("1\t\ta\n2\t\tb"));
        (_, Control control) = CreateCommentEditor(drawable, null);
        Show(control);

        string?[] texts = Find<TextBlock>(control).Select(t => t.Text).ToArray();
        Assert.That(texts, Does.Contain("コメント"));
        Assert.That(texts, Does.Contain("2 件"));
        // Without an editor (the settings page, the node graph) there is no tab to open.
        Assert.That(Find<Button>(control).Single().IsEnabled, Is.False);

        drawable.Comments.CurrentValue = drawable.Comments.CurrentValue.Add(new DanmakuComment(TimeSpan.Zero, "c"));
        Dispatcher.UIThread.RunJobs();
        Assert.That(Find<TextBlock>(control).Select(t => t.Text), Does.Contain("3 件"));
    }

    [AvaloniaTest]
    public void EditButtonOpensTheToolTabOnce()
    {
        using var f = new EditorFixture();
        (_, Control control) = CreateCommentEditor(f.Drawable, new Visitor(f.Context));
        Show(control);
        Button edit = Find<Button>(control).Single();
        Assert.That(edit.IsEnabled, Is.True);

        edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // The second click brings the same tab to the front instead of opening another.
        Assert.That(f.Context.OpenRequests, Has.Count.EqualTo(2));
        var tab = (DanmakuTabViewModel)f.Context.OpenRequests.Distinct().Single();
        Assert.That(tab.Target.Value, Is.SameAs(f.Drawable));

        // 編集 on another object switches the open tab to that object.
        DanmakuDrawable other = f.AddDanmaku("1.0\t\tべつ", TimeSpan.FromSeconds(20));
        (_, Control otherControl) = CreateCommentEditor(other, new Visitor(f.Context));
        Show(otherControl);
        Find<Button>(otherControl).Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.That(f.Context.OpenRequests.Distinct().Single(), Is.SameAs(tab));
        Assert.That(tab.Target.Value, Is.SameAs(other));
        tab.Dispose();
    }

    // Stands in for the property panel: answers IEditorContext with the fake editor.
    private sealed class Visitor(IServiceProvider services) : IPropertyEditorContextVisitor, IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IEditorContext) ? services as IEditorContext ?? services.GetService(serviceType) : services.GetService(serviceType);

        public void Visit(IPropertyEditorContext context)
        {
        }
    }
}

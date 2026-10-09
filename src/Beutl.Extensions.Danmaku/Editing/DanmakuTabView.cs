using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Beutl.Extensions.Danmaku.Comments;
using FluentIcons.Common;
using static Beutl.Extensions.Danmaku.Editing.BeutlUi;

namespace Beutl.Extensions.Danmaku.Editing;

/// <summary>
/// The view of the "コメント弾幕" tool tab, laid out like Beutl's own tool tabs. It has two pages: the editor
/// (the comment list and the selected comment) and the import page, which takes the whole tab like a browser's
/// download page. Its DataContext is a <see cref="DanmakuTabViewModel"/>.
/// </summary>
public sealed class DanmakuTabView : UserControl
{
    private const double TimeWidth = 84;
    private const double PositionWidth = 60;
    private const double SizeWidth = 48;
    private const double ColorWidth = 32;
    private const double DetailWidth = 380;
    private const double WideLayoutMinWidth = 820;

    // Filter box, column header and about three rows: the list never shrinks below this when stacked.
    private const double ListPaneMinHeight = 180;

    private static readonly FilePickerFileType s_commentFiles = new("コメントファイル")
    {
        Patterns = ["*.xml", "*.json", "*.txt", "*.tsv", "*.csv"],
    };

    private static readonly IValueConverter s_brushConverter =
        new FuncValueConverter<Color, IBrush>(c => new SolidColorBrush(c));

    private static readonly IValueConverter s_isZero = new FuncValueConverter<int, bool>(n => n == 0);

    private static readonly IValueConverter s_isPositive = new FuncValueConverter<int, bool>(n => n > 0);

    private readonly ListBox _list;
    private readonly Button _deleteButton;
    private readonly Button _locateButton;
    private readonly Grid _body;
    private readonly Control _detailNone;
    private readonly Control _detailMulti;
    private readonly TextBlock _detailMultiText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Control _detailSingle;
    private readonly List<TextBox> _detailFields = [];
    private readonly List<IDisposable> _subscriptions = [];
    private DanmakuTabViewModel? _viewModel;
    private bool _syncingSelection;
    private bool _wide = true;
    private bool _showDetail;
    private bool _layoutApplied;

    public DanmakuTabView()
    {
        _list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<CommentRowViewModel>(BuildRow, supportsRecycling: true),
            [!ItemsControl.ItemsSourceProperty] = new Binding(nameof(DanmakuTabViewModel.Rows)),
        };
        // Compact rows: a comment list is long, and each row is one line.
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(PaddingProperty, new Thickness(8, 4)),
                new Setter(MinHeightProperty, 0d),
            },
        });
        _list.KeyDown += OnListKeyDown;
        _list.SelectionChanged += OnSelectionChanged;
        _list.DoubleTapped += OnListDoubleTapped;

        _deleteButton = IconButton(Icon.Delete, "削除 (Delete)", DeleteSelected);
        _deleteButton.IsEnabled = false;
        _locateButton = IconButton(Icon.TargetArrow, "タイムラインで選択", () => ViewModel?.SelectTargetInTimeline());

        _detailNone = Secondary("コメントを選択してください");
        _detailMulti = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _detailMultiText, TextButton("削除", DeleteSelected) },
        };
        _detailSingle = BuildDetailForm();

        _body = new Grid();
        _body.Children.Add(BuildListPane());
        _body.Children.Add(new ScrollViewer
        {
            IsVisible = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = Group(new StackPanel { Spacing = 8, Children = { _detailNone, _detailMulti, _detailSingle } }),
        });
        _body[!IsVisibleProperty] = new Binding("Target.Value") { Converter = ObjectConverters.IsNotNull };

        TextBlock noTargetTitle = EmptyTitle();
        noTargetTitle[!TextBlock.TextProperty] = new Binding("TargetMessage.Value");
        StackPanel noTarget = EmptyState(Icon.CommentMultiple, noTargetTitle);
        noTarget[!IsVisibleProperty] = new Binding("Target.Value") { Converter = ObjectConverters.IsNull };

        TextBlock status = Secondary();
        status.Margin = new Thickness(0, 0, 0, 8);
        status[!TextBlock.TextProperty] = new Binding("Status.Value");
        status[!IsVisibleProperty] = new Binding("Status.Value") { Converter = StringConverters.IsNotNullOrEmpty };

        var content = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
        DockPanel.SetDock(status, Dock.Top);
        content.Children.Add(status);
        content.Children.Add(new Panel { Children = { _body, noTarget } });

        var editorPage = new DockPanel { LastChildFill = true };
        Border bar = BuildEditorBar();
        DockPanel.SetDock(bar, Dock.Top);
        editorPage.Children.Add(bar);
        editorPage.Children.Add(content);
        editorPage[!IsVisibleProperty] = new Binding("IsImportPageOpen.Value") { Converter = BoolConverters.Not };

        Control importPage = BuildImportPage();
        importPage[!IsVisibleProperty] = new Binding("IsImportPageOpen.Value");

        Content = new Panel { Children = { editorPage, importPage } };

        ApplyLayout(wide: true, showDetail: false);
        UpdateDetail();
    }

    private DanmakuTabViewModel? ViewModel => DataContext as DanmakuTabViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel != null)
            _viewModel.RowFocusRequested -= OnRowFocusRequested;
        foreach (IDisposable subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();

        _viewModel = ViewModel;
        if (_viewModel != null)
        {
            _viewModel.RowFocusRequested += OnRowFocusRequested;
            // The tab does not follow the timeline selection; the locate button says which element it edits.
            _subscriptions.Add(_viewModel.TargetDescription.Subscribe(description =>
                ToolTip.SetTip(_locateButton, string.IsNullOrEmpty(description) ? "タイムラインで選択" : $"タイムラインで選択（{description}）")));
            _subscriptions.Add(_viewModel.TotalCount.Subscribe(count => ApplyLayout(_wide, showDetail: count > 0)));
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyLayout(e.NewSize.Width >= WideLayoutMinWidth, _showDetail);
    }

    // Side by side when there is room (the tab docks under the timeline by default), stacked when narrow.
    private void ApplyLayout(bool wide, bool showDetail)
    {
        if (_layoutApplied && _wide == wide && _showDetail == showDetail)
            return;

        _layoutApplied = true;
        _wide = wide;
        _showDetail = showDetail;
        Control list = _body.Children[0];
        Control detail = _body.Children[1];
        detail.IsVisible = showDetail;
        if (wide)
        {
            _body.RowDefinitions = [];
            _body.ColumnDefinitions = new ColumnDefinitions(showDetail ? $"*,8,{DetailWidth}" : "*,0,0");
            Grid.SetRow(detail, 0);
            Grid.SetColumn(detail, 2);
        }
        else
        {
            // The list and the editor share the height and each scrolls; an Auto editor row would take all of it,
            // squeezing the list to nothing and cutting the editor off.
            _body.ColumnDefinitions = [];
            _body.RowDefinitions = showDetail
                ? [new RowDefinition(2, GridUnitType.Star) { MinHeight = ListPaneMinHeight }, new RowDefinition(8, GridUnitType.Pixel), new RowDefinition(3, GridUnitType.Star)]
                : new RowDefinitions("*,0,0");
            Grid.SetRow(detail, 2);
            Grid.SetColumn(detail, 0);
        }

        Grid.SetRow(list, 0);
        Grid.SetColumn(list, 0);
    }

    // ---------------------------------------------------------------- editor page

    private Border BuildEditorBar()
    {
        TextBlock count = Secondary();
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(0, 0, 4, 0);
        count[!TextBlock.TextProperty] = new Binding("Summary.Value");

        var export = new MenuItem { Header = "ファイルに書き出し…" };
        export.Click += (_, _) => ExportAsync();
        var clear = new MenuItem { Header = "すべて削除" };
        clear.Click += (_, _) => ViewModel?.ClearAll();
        Button more = IconButton(Icon.MoreVertical, "その他", () => { });
        more.Flyout = new MenuFlyout { Items = { export, new Separator(), clear } };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                count,
                IconButton(Icon.Add, "追加", () => ViewModel?.AddAtCurrentTime()),
                _deleteButton,
                IconButton(Icon.ArrowDownload, "コメントを取り込む", () => ViewModel?.OpenImportPage()),
                _locateButton,
                more,
            },
            [!IsVisibleProperty] = new Binding("Target.Value") { Converter = ObjectConverters.IsNotNull },
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(actions, 1);
        row.Children.Add(BarTitle("コメント弾幕"));
        row.Children.Add(actions);
        return ToolBar(row);
    }

    private Control BuildListPane()
    {
        var filter = new TextBox
        {
            PlaceholderText = "絞り込み",
            InnerLeftContent = new FluentIcons.Avalonia.Fluent.FluentIcon { Icon = Icon.Search, FontSize = 14, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.6 },
            Margin = new Thickness(0, 0, 0, 8),
            [!TextBox.TextProperty] = new Binding("Filter.Value") { Mode = BindingMode.TwoWay },
        };
        filter.Classes.Add("clearButton");

        Grid header = Columns(Secondary("時刻"), Secondary("位置"), Secondary("大きさ"), Secondary("色"), Secondary("本文"));
        header.Margin = new Thickness(9, 0, 9, 4);

        TextBlock emptyTitle = EmptyTitle();
        emptyTitle[!TextBlock.TextProperty] = new Binding("EmptyMessage.Value");
        Button import = TextButton("取り込む…", () => ViewModel?.OpenImportPage(), accent: true);
        import[!IsVisibleProperty] = new Binding("TotalCount.Value") { Converter = s_isZero };
        StackPanel empty = EmptyState(Icon.Comment, emptyTitle, import);
        empty[!IsVisibleProperty] = new Binding("EmptyMessage.Value") { Converter = StringConverters.IsNotNullOrEmpty };

        var pane = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(filter, Dock.Top);
        DockPanel.SetDock(header, Dock.Top);
        pane.Children.Add(filter);
        pane.Children.Add(header);
        pane.Children.Add(Group(new Panel { Children = { _list, empty } }, new Thickness(0)));
        return pane;
    }

    private static Grid Columns(params Control[] cells)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{TimeWidth},{PositionWidth},{SizeWidth},{ColorWidth},*"),
        };
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            grid.Children.Add(cells[i]);
        }

        return grid;
    }

    private static Control BuildRow(CommentRowViewModel row, INameScope scope)
    {
        TextBlock Cell(string path, bool secondary = false)
        {
            TextBlock cell = secondary ? Secondary() : new TextBlock();
            cell.FontSize = 14;
            cell.TextWrapping = TextWrapping.NoWrap;
            cell.VerticalAlignment = VerticalAlignment.Center;
            cell.TextTrimming = TextTrimming.CharacterEllipsis;
            cell[!TextBlock.TextProperty] = new Binding(path);
            return cell;
        }

        var swatch = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            [!Border.BackgroundProperty] = new Binding(nameof(CommentRowViewModel.SwatchColor)) { Converter = s_brushConverter },
        };
        WithResource(swatch, Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");

        return Columns(
            Cell(nameof(CommentRowViewModel.TimeText), secondary: true),
            Cell(nameof(CommentRowViewModel.PositionName), secondary: true),
            Cell(nameof(CommentRowViewModel.SizeName), secondary: true),
            swatch,
            Cell(nameof(CommentRowViewModel.SingleLineText)));
    }

    private Control BuildDetailForm()
    {
        Grid form = Rows();

        TextBox time = Field(nameof(CommentRowViewModel.TimeText));
        time.Width = 100;
        AddRow(form, 0, "時刻", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                time,
                IconButton(Icon.ClockArrowDownload, "再生位置にする", () => WithRow(r => ViewModel?.SetTimeToPlayhead(r))),
                IconButton(Icon.ArrowStepIn, "この時刻へ移動", () => WithRow(r => ViewModel?.Seek(r))),
            },
        });
        AddRow(form, 1, "位置", Choice(DanmakuTabViewModel.PositionNames, nameof(CommentRowViewModel.PositionIndex)));
        AddRow(form, 2, "大きさ", Choice(DanmakuTabViewModel.SizeNames, nameof(CommentRowViewModel.SizeIndex)));

        var palette = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach ((string name, string displayName, Beutl.Media.Color color) in CommentCommands.StandardColors)
        {
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
            };
            WithResource(swatch, Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            var button = new Button { Padding = new Thickness(3), Margin = new Thickness(0, 0, 2, 2), MinWidth = 0, Content = swatch };
            WithResource(button, StyledElement.ThemeProperty, "ToolTabBarIconButtonTheme");
            ToolTip.SetTip(button, displayName);
            string value = name == "white" ? "" : CommentCommands.FormatColor(color);
            button.Click += (_, _) => WithRow(r => r.ColorText = value);
            palette.Children.Add(button);
        }

        TextBox colorText = Field(nameof(CommentRowViewModel.ColorText));
        colorText.Width = 100;
        colorText.PlaceholderText = "白";
        colorText.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(colorText, "#RRGGBB または色名");
        AddRow(form, 3, "色", new StackPanel { Spacing = 2, Children = { palette, colorText } }, alignTop: true);

        TextBox text = Field(nameof(CommentRowViewModel.Text));
        text.TextWrapping = TextWrapping.Wrap;
        text.MinHeight = 56;
        ToolTip.SetTip(text, "Shift+Enter で改行");
        AddRow(form, 4, "本文", text, alignTop: true);
        return form;
    }

    private static ComboBox Choice(IReadOnlyList<string> items, string path) => new()
    {
        ItemsSource = items,
        MinWidth = 120,
        [!Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty] = new Binding(path) { Mode = BindingMode.TwoWay },
    };

    private void WithRow(Action<CommentRowViewModel> action)
    {
        if (_detailSingle.DataContext is CommentRowViewModel row)
            action(row);
    }

    private TextBox Field(string path)
    {
        var box = new TextBox
        {
            [!TextBox.TextProperty] = new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
        };
        box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && box.TextWrapping == TextWrapping.Wrap)
            {
                // Shift+Enter breaks the line in the comment text.
                string current = box.Text ?? "";
                int start = Math.Min(box.SelectionStart, box.SelectionEnd);
                int end = Math.Max(box.SelectionStart, box.SelectionEnd);
                box.Text = current[..start] + "\n" + current[end..];
                box.CaretIndex = start + 1;
                return;
            }

            BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        }, RoutingStrategies.Tunnel);
        _detailFields.Add(box);
        return box;
    }

    // ---------------------------------------------------------------- selection

    private IReadOnlyList<CommentRowViewModel> SelectedRows() =>
        _list.SelectedItems?.OfType<CommentRowViewModel>().ToArray() ?? [];

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
            return;

        // The editor still shows the previous comment; save what was typed into it before it switches.
        CommentRowViewModel[] wanted = SelectedRows().ToArray();
        DanmakuComment[] wantedComments = wanted.Select(r => r.Comment).ToArray();
        CommitPendingEdits();

        // Saving can move comments between rows (a time edit re-sorts the list); keep the clicked comments selected.
        if (!wanted.Select(r => r.Comment).SequenceEqual(wantedComments))
        {
            _syncingSelection = true;
            try
            {
                _list.SelectedItems!.Clear();
                foreach (CommentRowViewModel row in _list.Items.OfType<CommentRowViewModel>())
                {
                    if (wantedComments.Any(c => ReferenceEquals(c, row.Comment)))
                        _list.SelectedItems.Add(row);
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        UpdateDetail();
    }

    private void OnRowFocusRequested(CommentRowViewModel row)
    {
        if (_syncingSelection)
            return;

        // Raised from inside a binding update; select once that update has finished.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_list.Items.Contains(row))
                return;

            _list.SelectedItems!.Clear();
            _list.SelectedItem = row;
            _list.ScrollIntoView(row);
        });
    }

    private void UpdateDetail()
    {
        IReadOnlyList<CommentRowViewModel> rows = SelectedRows();
        _detailNone.IsVisible = rows.Count == 0;
        _detailMulti.IsVisible = rows.Count > 1;
        _detailMultiText.Text = $"{rows.Count} 件を選択中";
        _detailSingle.IsVisible = rows.Count == 1;
        _detailSingle.DataContext = rows.Count == 1 ? rows[0] : null;
        _deleteButton.IsEnabled = rows.Count > 0;
    }

    private void CommitPendingEdits()
    {
        if (_detailSingle.DataContext is not CommentRowViewModel)
            return;

        _syncingSelection = true;
        try
        {
            foreach (TextBox box in _detailFields)
                BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is CommentRowViewModel row)
            vm.Seek(row);
    }

    private void DeleteSelected()
    {
        if (ViewModel is { } vm && SelectedRows() is { Count: > 0 } rows)
            vm.Delete(rows);
    }

    // ---------------------------------------------------------------- import page

    private Control BuildImportPage()
    {
        var url = new TextBox { PlaceholderText = "ニコニコ動画・YouTube・Twitch の URL" };
        url.Classes.Add("clearButton");
        url[!TextBox.TextProperty] = new Binding("Url.Value") { Mode = BindingMode.TwoWay };
        url.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && ViewModel is { } vm)
            {
                e.Handled = true;
                await vm.DownloadAsync();
            }
        };
        Button download = TextButton("ダウンロード", async () => await (ViewModel?.DownloadAsync() ?? Task.CompletedTask), accent: true);
        download[!IsEnabledProperty] = new Binding("IsDownloading.Value") { Converter = BoolConverters.Not };
        var urlRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(download, 1);
        urlRow.Children.Add(url);
        urlRow.Children.Add(download);

        Button file = TextButton("ファイルを選ぶ…", ImportAsync);
        file.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(file, "XML・JSON・テキスト");

        var mode = new ComboBox
        {
            ItemsSource = new[] { "置き換える", "追加する" },
            MinWidth = 140,
            [!Avalonia.Controls.Primitives.SelectingItemsControl.SelectedIndexProperty] = new Binding("ReplaceOnImport.Value") { Mode = BindingMode.TwoWay, Converter = ReplaceModeConverter.Instance },
        };

        Grid sources = Rows();
        AddRow(sources, 0, "URL", urlRow);
        AddRow(sources, 1, "ファイル", file);
        AddRow(sources, 2, "既存のコメント", mode);

        TextBlock notice = Secondary();
        WithResource(notice, TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        notice[!TextBlock.TextProperty] = new Binding("ImportNotice.Value");
        notice[!IsVisibleProperty] = new Binding("ImportNotice.Value") { Converter = StringConverters.IsNotNullOrEmpty };

        TextBlock historyTitle = new() { Text = "履歴", [!IsVisibleProperty] = new Binding("ImportJobs.Count") { Converter = s_isPositive } };
        var jobs = new ItemsControl
        {
            // Rows draw a separator above themselves; the first one tucks under the group's border.
            Margin = new Thickness(0, -1, 0, 0),
            ItemTemplate = new FuncDataTemplate<ImportJobViewModel>(BuildJob, supportsRecycling: false),
            [!ItemsControl.ItemsSourceProperty] = new Binding(nameof(DanmakuTabViewModel.ImportJobs)),
        };
        Border history = Group(jobs, new Thickness(0));
        history[!IsVisibleProperty] = new Binding("ImportJobs.Count") { Converter = s_isPositive };

        return BuildPage("コメントを取り込む", new StackPanel
        {
            Spacing = 12,
            Children = { Group(sources), notice, historyTitle, history },
        });
    }

    // A full-tab page with a back button, like a browser's download page.
    private Control BuildPage(string title, Control content)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        TextBlock titleText = BarTitle(title);
        Grid.SetColumn(titleText, 1);
        row.Children.Add(IconButton(Icon.ArrowLeft, "戻る (Esc)", () => ViewModel?.CloseImportPage()));
        row.Children.Add(titleText);
        Border bar = ToolBar(row);

        var page = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(bar, Dock.Top);
        page.Children.Add(bar);
        page.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(8), Child = content },
        });
        page.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                ViewModel?.CloseImportPage();
                e.Handled = true;
            }
        };
        return page;
    }

    private static Control BuildJob(ImportJobViewModel job, INameScope scope)
    {
        TextBlock message = Secondary();
        message[!TextBlock.TextProperty] = new Binding(nameof(ImportJobViewModel.Message));
        TextBlock caption = Secondary();
        caption[!TextBlock.TextProperty] = new Binding(nameof(ImportJobViewModel.Caption));
        var details = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, [!TextBlock.TextProperty] = new Binding(nameof(ImportJobViewModel.Title)) },
                caption,
                message,
                new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 4, 0, 0), [!IsVisibleProperty] = new Binding(nameof(ImportJobViewModel.IsRunning)) },
            },
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        row.Children.Add(details);
        Border pill = StatePill(job);
        Grid.SetColumn(pill, 1);
        row.Children.Add(pill);
        if (job.CanCancel)
        {
            Button cancel = IconButton(Icon.Dismiss, "中止", job.Cancel);
            cancel.VerticalAlignment = VerticalAlignment.Top;
            cancel[!IsVisibleProperty] = new Binding(nameof(ImportJobViewModel.IsRunning));
            Grid.SetColumn(cancel, 2);
            row.Children.Add(cancel);
        }

        var item = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        return WithResource(item, Border.BorderBrushProperty, "DividerStrokeColorDefaultBrush");
    }

    // The coloured state label of Beutl's lists (the proxies tab, for one).
    private static Border StatePill(ImportJobViewModel job)
    {
        var text = new TextBlock { FontSize = 12 };
        var pill = new Border
        {
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Top,
            Child = text,
        };
        IDisposable? background = null;

        void Update()
        {
            (string label, string key) = job.State switch
            {
                ImportJobState.Running => ("取得中", "SubtleFillColorSecondaryBrush"),
                ImportJobState.Succeeded => ("完了", "SystemFillColorSuccessBackgroundBrush"),
                ImportJobState.Failed => ("失敗", "SystemFillColorCriticalBackgroundBrush"),
                _ => ("中止", "SystemFillColorCautionBackgroundBrush"),
            };
            text.Text = label;
            background?.Dispose();
            background = pill.Bind(Border.BackgroundProperty, pill.GetResourceObservable(key));
        }

        Update();
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImportJobViewModel.State))
                Update();
        };
        return pill;
    }

    // ---------------------------------------------------------------- files

    private async void ImportAsync()
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "コメントファイルを開く",
            AllowMultiple = false,
            FileTypeFilter = [s_commentFiles, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            vm.ImportFile(path);
    }

    private async void ExportAsync()
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;

        IStorageFile? file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "コメントを書き出す",
            SuggestedFileName = "comments.txt",
            DefaultExtension = "txt",
            FileTypeChoices = [new FilePickerFileType("テキスト形式のコメント") { Patterns = ["*.txt"] }],
        });
        if (file?.TryGetLocalPath() is { } path)
            vm.ExportFile(path);
    }

    /// <summary>"置き換える" (index 0) when imports replace the comments, "追加する" (index 1) when they add to them.</summary>
    private sealed class ReplaceModeConverter : IValueConverter
    {
        public static readonly ReplaceModeConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is false ? 1 : 0;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is int index ? index == 0 : BindingOperations.DoNothing;
    }
}

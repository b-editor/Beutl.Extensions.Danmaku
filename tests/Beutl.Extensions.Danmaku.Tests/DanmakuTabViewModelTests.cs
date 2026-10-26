using System.Text.Json.Nodes;
using Avalonia.Headless.NUnit;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Download;
using Beutl.Extensions.Danmaku.Editing;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.ProjectSystem;

namespace Beutl.Extensions.Danmaku.Tests;

// Runs on the Avalonia UI thread, where the editor runs the tool tab.
public class DanmakuTabViewModelTests
{
    private static string WriteFile(string content)
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"import-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        return path;
    }

    [AvaloniaTest]
    public void TheTabEditsTheObjectItWasOpenedFor()
    {
        using var f = new EditorFixture();
        Assert.That(f.ViewModel.Target.Value, Is.Null);
        Assert.That(f.ViewModel.TargetMessage.Value, Does.Contain("「編集」"));

        f.ViewModel.SetTarget(f.Drawable);
        Assert.That(f.ViewModel.Target.Value, Is.SameAs(f.Drawable));
        Assert.That(f.ViewModel.TargetElement.Value, Is.SameAs(f.Element));
        Assert.That(f.ViewModel.TargetMessage.Value, Is.Null);
        Assert.That(f.ViewModel.TargetDescription.Value, Is.EqualTo("0:01.000 から始まる要素"));
        Assert.That(f.ViewModel.Rows.Select(r => r.Text), Is.EqualTo(new[] { "いち", "赤いコメント", "さん" }));
        Assert.That(f.ViewModel.Rows[1].ColorText, Is.EqualTo("#FF0000"));
        Assert.That(f.ViewModel.Summary.Value, Is.EqualTo("3 件"));

        // Selecting other things on the timeline does not change what the tab edits.
        DanmakuDrawable other = f.AddDanmaku("1.0\t\tべつ", TimeSpan.FromSeconds(20));
        f.Selection.Select(other.FindHierarchicalParent<Element>());
        f.Selection.Select(null);
        Assert.That(f.ViewModel.Target.Value, Is.SameAs(f.Drawable));

        // Opening it for another object switches to that one.
        f.ViewModel.SetTarget(other);
        Assert.That(f.ViewModel.Rows.Select(r => r.Text), Is.EqualTo(new[] { "べつ" }));
        Assert.That(f.ViewModel.TargetDescription.Value, Is.EqualTo("0:20.000 から始まる要素"));
    }

    [AvaloniaTest]
    public void DeletingTheElementHidesTheEditorUntilUndo()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);

        f.Scene.RemoveChild(f.Element);
        f.History.Commit("delete");
        Assert.That(f.ViewModel.Target.Value, Is.Null);
        Assert.That(f.ViewModel.Rows, Is.Empty);
        Assert.That(f.ViewModel.TargetMessage.Value, Does.Contain("削除されました"));

        Assert.That(f.History.Undo(), Is.True);
        Assert.That(f.ViewModel.Target.Value, Is.SameAs(f.Drawable));
        Assert.That(f.ViewModel.Rows, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    public void SelectTargetInTimelineSelectsTheEditedElement()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);

        f.ViewModel.SelectTargetInTimeline();

        Assert.That(f.Selection.SelectedObject.Value, Is.SameAs(f.Element));
    }

    [AvaloniaTest]
    public void ARestoredTabFindsItsObjectAgain()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);
        var json = new JsonObject();
        f.ViewModel.WriteToJson(json);

        // The host restores the tab with the layout: a new context reading the saved state.
        using var restored = new DanmakuTabViewModel(new DanmakuTabExtension(), f.Context);
        restored.ReadFromJson(json);

        Assert.That(restored.Target.Value, Is.SameAs(f.Drawable));
        Assert.That(restored.Rows, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    public async Task WithoutATargetNothingIsImportedOrDownloaded()
    {
        bool called = false;
        using var f = new EditorFixture(download: (_, _, _) =>
        {
            called = true;
            return Task.FromResult(new List<DanmakuComment>());
        });
        f.ViewModel.Url.Value = "https://www.nicovideo.jp/watch/sm9";

        await f.ViewModel.DownloadAsync();
        Assert.That(called, Is.False);
        Assert.That(f.ViewModel.Status.Value, Does.Contain("対象がありません"));

        f.ViewModel.Status.Value = null;
        f.ViewModel.ImportFile(WriteFile("1.0\t\tファイル"));
        Assert.That(f.ViewModel.Status.Value, Does.Contain("対象がありません"));
        Assert.That(f.Scene.Children, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ADownloadOnlyLandsInTheObjectItStartedFor()
    {
        var pending = new TaskCompletionSource<List<DanmakuComment>>();
        using var f = new EditorFixture(download: (_, _, _) => pending.Task);
        f.ViewModel.SetTarget(f.Drawable);
        f.ViewModel.Url.Value = "https://www.nicovideo.jp/watch/sm9";

        Task download = f.ViewModel.DownloadAsync();
        DanmakuDrawable other = f.AddDanmaku("1.0\t\tべつ", TimeSpan.FromSeconds(20));
        f.ViewModel.SetTarget(other);
        pending.SetResult([new DanmakuComment(TimeSpan.FromSeconds(1), "取得")]);
        await download;

        Assert.That(other.Comments.Value.Items.Select(c => c.Text), Is.EqualTo(new[] { "べつ" }));
        Assert.That(f.Texts, Has.Length.EqualTo(3));
        Assert.That(f.ViewModel.Status.Value, Does.Contain("反映していません"));
    }

    [AvaloniaTest]
    public void TheImportPageOpensOnlyForATargetAndClosesWithIt()
    {
        using var f = new EditorFixture();
        f.ViewModel.OpenImportPage();
        Assert.That(f.ViewModel.IsImportPageOpen.Value, Is.False, "nothing to import into");

        f.ViewModel.SetTarget(f.Drawable);
        f.ViewModel.OpenImportPage();
        Assert.That(f.ViewModel.IsImportPageOpen.Value, Is.True);
        f.ViewModel.CloseImportPage();
        Assert.That(f.ViewModel.IsImportPageOpen.Value, Is.False);

        // Deleting the element leaves nothing to import into either.
        f.ViewModel.OpenImportPage();
        f.Scene.RemoveChild(f.Element);
        Assert.That(f.ViewModel.IsImportPageOpen.Value, Is.False);
    }

    [AvaloniaTest]
    public async Task ImportsAreListedNewestFirstWithTheirResults()
    {
        using var f = new EditorFixture(download: (url, _, _) => url.Contains("youtu")
            ? Task.FromException<List<DanmakuComment>>(new CommentDownloadException("リプレイがありません"))
            : Task.FromResult(Enumerable.Range(0, 1234).Select(i => new DanmakuComment(TimeSpan.FromSeconds(i), "w")).ToList()));
        f.ViewModel.SetTarget(f.Drawable);

        f.ViewModel.ImportFile(WriteFile("1.0\t\tいち"));
        f.ViewModel.Url.Value = "https://youtu.be/dQw4w9WgXcQ";
        await f.ViewModel.DownloadAsync();
        f.ViewModel.ReplaceOnImport.Value = false;
        f.ViewModel.Url.Value = "https://www.twitch.tv/videos/123";
        await f.ViewModel.DownloadAsync();

        Assert.That(f.ViewModel.ImportJobs.Select(j => (j.Source, j.State, j.Message)), Is.EqualTo(new[]
        {
            ("Twitch", ImportJobState.Succeeded, "1,234 件を追加しました"),
            ("YouTube", ImportJobState.Failed, "リプレイがありません"),
            ("ファイル", ImportJobState.Succeeded, "1 件を取り込みました"),
        }));
        Assert.That(f.ViewModel.ImportJobs[0].Title, Is.EqualTo("https://www.twitch.tv/videos/123"));

        // Another object has its own history.
        f.ViewModel.SetTarget(f.AddDanmaku("1.0\t\tべつ", TimeSpan.FromSeconds(20)));
        Assert.That(f.ViewModel.ImportJobs, Is.Empty);
    }

    [AvaloniaTest]
    public async Task AnUnsupportedUrlIsANoticeNotAHistoryEntry()
    {
        using var f = new EditorFixture(download: (_, _, _) => Task.FromResult(new List<DanmakuComment>()));
        f.ViewModel.SetTarget(f.Drawable);
        f.ViewModel.Url.Value = "https://example.com/video";

        await f.ViewModel.DownloadAsync();
        Assert.That(f.ViewModel.ImportNotice.Value, Does.Contain("対応していない URL"));
        Assert.That(f.ViewModel.ImportJobs, Is.Empty);

        f.ViewModel.ImportFile(WriteFile("1.0\t\tいち"));
        Assert.That(f.ViewModel.ImportNotice.Value, Is.Null, "a new import clears the notice");
    }

    [AvaloniaTest]
    public void EditingARowIsOneUndoableChange()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);
        CommentRowViewModel row = f.ViewModel.Rows[0];

        row.Text = "かえた";
        row.SizeIndex = 1;
        Assert.That(f.Texts, Is.EqualTo(new[] { "かえた", "赤いコメント", "さん" }));
        Assert.That(f.Drawable.Comments.Value[0].Size, Is.EqualTo(CommentSize.Big));

        Assert.That(f.History.Undo(), Is.True);
        Assert.That(f.Drawable.Comments.Value[0].Size, Is.EqualTo(CommentSize.Medium));
        Assert.That(f.History.Undo(), Is.True);
        Assert.That(f.Texts[0], Is.EqualTo("いち"));
        // Undo changes the list from outside the tab; the table follows it.
        Assert.That(row.Text, Is.EqualTo("いち"));

        Assert.That(f.History.Redo(), Is.True);
        Assert.That(row.Text, Is.EqualTo("かえた"));
    }

    [AvaloniaTest]
    public void EditingTheTimeMovesTheComment()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);

        f.ViewModel.Rows[0].TimeText = "0:05";

        Assert.That(f.Texts, Is.EqualTo(new[] { "赤いコメント", "さん", "いち" }));
        Assert.That(f.ViewModel.Rows.Select(r => r.Text), Is.EqualTo(f.Texts));
        Assert.That(f.ViewModel.Rows[2].TimeText, Is.EqualTo("0:05.000"));
    }

    [AvaloniaTest]
    [TestCase("TimeText", "abc")]
    [TestCase("ColorText", "reddish")]
    [TestCase("Text", "  ")]
    public void UnreadableInputIsRevertedWithAMessage(string field, string value)
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);
        DanmakuCommentList before = f.Drawable.Comments.Value;
        CommentRowViewModel row = f.ViewModel.Rows[1];

        typeof(CommentRowViewModel).GetProperty(field)!.SetValue(row, value);

        Assert.That(f.Drawable.Comments.Value, Is.SameAs(before));
        Assert.That(f.History.CanUndo, Is.False);
        Assert.That((row.TimeText, row.ColorText, row.Text), Is.EqualTo(("0:02.000", "#FF0000", "赤いコメント")));
        Assert.That(f.ViewModel.Status.Value, Does.Contain("元に戻しました"));
    }

    [AvaloniaTest]
    public void AddPutsACommentAtThePlayheadInElementTime()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);
        f.Drawable.TimeOffset.Value = MediaTime.FromSeconds(0.5);
        f.Clock.Seek(MediaTime.FromSeconds(3));
        CommentRowViewModel? added = null;
        f.ViewModel.RowFocusRequested += row => added = row;

        f.ViewModel.AddAtCurrentTime();

        // The element starts at 1 s and the comments are offset by 0.5 s.
        DanmakuComment comment = f.Drawable.Comments.Value.Items.Single(c => c.Text == "コメント");
        Assert.That(comment.Time, Is.EqualTo(TimeSpan.FromSeconds(1.5)));
        Assert.That(added, Is.Not.Null);
        Assert.That(added!.Comment, Is.SameAs(comment));
        Assert.That(f.ViewModel.Rows, Has.Count.EqualTo(4));

        f.Clock.Seek(MediaTime.Zero);
        f.ViewModel.AddAtCurrentTime();
        Assert.That(f.Drawable.Comments.Value[0].Time, Is.EqualTo(TimeSpan.Zero));
    }

    [AvaloniaTest]
    public void DeleteAndClearAreUndoable()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);

        f.ViewModel.Delete([f.ViewModel.Rows[0], f.ViewModel.Rows[2]]);
        Assert.That(f.Texts, Is.EqualTo(new[] { "赤いコメント" }));
        Assert.That(f.ViewModel.Rows, Has.Count.EqualTo(1));

        f.ViewModel.ClearAll();
        Assert.That(f.Texts, Is.Empty);
        Assert.That(f.ViewModel.Summary.Value, Is.EqualTo("0 件"));

        f.History.Undo();
        f.History.Undo();
        Assert.That(f.Texts, Has.Length.EqualTo(3));
        Assert.That(f.ViewModel.Rows, Has.Count.EqualTo(3));
    }

    [AvaloniaTest]
    public void FilterNarrowsTheRows()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);

        f.ViewModel.Filter.Value = "コメント";
        Assert.That(f.ViewModel.Rows.Select(r => r.Text), Is.EqualTo(new[] { "赤いコメント" }));
        Assert.That(f.ViewModel.Summary.Value, Is.EqualTo("1 / 3 件"));

        // Rows keep their index into the full list, so deleting a filtered row deletes the right comment.
        f.ViewModel.Delete(f.ViewModel.Rows.ToArray());
        Assert.That(f.Texts, Is.EqualTo(new[] { "いち", "さん" }));
        Assert.That(f.ViewModel.Rows, Is.Empty);

        f.ViewModel.Filter.Value = "";
        Assert.That(f.ViewModel.Rows, Has.Count.EqualTo(2));
    }

    [AvaloniaTest]
    public void ImportReplacesOrAppends()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);
        string file = WriteFile("0.5\tblue\tとりこみ\n4.0\t\tもうひとつ");

        f.ViewModel.ImportFile(file);
        Assert.That(f.Texts, Is.EqualTo(new[] { "とりこみ", "もうひとつ" }));
        Assert.That(f.ViewModel.Status.Value, Does.Contain("2 件"));

        f.ViewModel.ReplaceOnImport.Value = false;
        f.ViewModel.ImportFile(file);
        Assert.That(f.Texts, Is.EqualTo(new[] { "とりこみ", "とりこみ", "もうひとつ", "もうひとつ" }));

        f.History.Undo();
        f.History.Undo();
        Assert.That(f.Texts, Is.EqualTo(new[] { "いち", "赤いコメント", "さん" }));
    }

    [AvaloniaTest]
    public void ImportProblemsAreReportedWithoutTouchingTheComments()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);

        f.ViewModel.ImportFile(WriteFile("# 何もない"));
        Assert.That(f.ViewModel.Status.Value, Does.Contain("見つかりませんでした"));

        f.ViewModel.ImportFile(Path.Combine(TestContext.CurrentContext.WorkDirectory, "missing.txt"));
        Assert.That(f.ViewModel.Status.Value, Does.Contain("読み込めませんでした"));

        f.ViewModel.ImportFile(WriteFile("<packet><chat vpos=\"100\">broken"));
        Assert.That(f.ViewModel.Status.Value, Does.Contain("読み込めませんでした"));

        Assert.That(f.Texts, Has.Length.EqualTo(3));
        Assert.That(f.History.CanUndo, Is.False);
    }

    [AvaloniaTest]
    public void TimeEditsAskTheViewToFollowTheMovedComment()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);
        CommentRowViewModel? focused = null;
        f.ViewModel.RowFocusRequested += row => focused = row;

        f.ViewModel.Rows[0].TimeText = "0:05";

        // "いち" moved from the first row to the last.
        Assert.That(focused, Is.SameAs(f.ViewModel.Rows[2]));
        Assert.That(focused!.Text, Is.EqualTo("いち"));
    }

    [AvaloniaTest]
    public void SeekAndSetTimeUseTheTimelinePosition()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);
        f.Drawable.TimeOffset.Value = MediaTime.FromSeconds(0.5);

        // "赤いコメント" is at 2 s; the element starts at 1 s and the comments are offset by 0.5 s.
        f.ViewModel.Seek(f.ViewModel.Rows[1]);
        Assert.That(f.Clock.CurrentTime.Value, Is.EqualTo(MediaTime.FromSeconds(3.5)));

        f.Clock.Seek(MediaTime.FromSeconds(6));
        f.ViewModel.SetTimeToPlayhead(f.ViewModel.Rows[1]);
        Assert.That(f.Drawable.Comments.Value.Items.Single(c => c.Text == "赤いコメント").Time, Is.EqualTo(TimeSpan.FromSeconds(4.5)));
    }

    [AvaloniaTest]
    public void EmptyListsExplainWhatToDo()
    {
        using var f = new EditorFixture("");
        Assert.That(f.ViewModel.EmptyMessage.Value, Is.Null, "Nothing to explain without a target.");

        f.ViewModel.SetTarget(f.Drawable);
        Assert.That(f.ViewModel.TotalCount.Value, Is.Zero);
        Assert.That(f.ViewModel.EmptyMessage.Value, Is.EqualTo("コメントがありません"));

        f.ViewModel.AddAtCurrentTime();
        Assert.That(f.ViewModel.EmptyMessage.Value, Is.Null);
        f.ViewModel.Filter.Value = "一致しない";
        Assert.That(f.ViewModel.EmptyMessage.Value, Does.Contain("一致する"));
    }

    [AvaloniaTest]
    public void ExportWritesAFileThatImportsBack()
    {
        using var f = new EditorFixture();
        f.ViewModel.SetTarget(f.Drawable);
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"export-{Guid.NewGuid():N}.txt");

        f.ViewModel.ExportFile(path);

        Assert.That(new DanmakuCommentList(CommentFile.Read(path)), Is.EqualTo(f.Drawable.Comments.Value));
        Assert.That(f.ViewModel.Status.Value, Does.Contain("3 件"));
    }

    [AvaloniaTest]
    public async Task DownloadReplacesTheComments()
    {
        string? requested = null;
        using var f = new EditorFixture(download: (url, progress, _) =>
        {
            requested = url;
            progress.Report("取得中");
            return Task.FromResult(new List<DanmakuComment> { new(TimeSpan.FromSeconds(1), "ダウンロード") });
        });
        f.ViewModel.SetTarget(f.Drawable);
        f.ViewModel.Url.Value = "  https://www.nicovideo.jp/watch/sm9 ";

        await f.ViewModel.DownloadAsync();

        Assert.That(requested, Is.EqualTo("https://www.nicovideo.jp/watch/sm9"));
        Assert.That(f.Texts, Is.EqualTo(new[] { "ダウンロード" }));
        Assert.That(f.ViewModel.Status.Value, Is.EqualTo("1 件を取り込みました"));
        Assert.That(f.ViewModel.IsDownloading.Value, Is.False);
    }

    [AvaloniaTest]
    public async Task UnsupportedUrlsAreRejectedBeforeDownloading()
    {
        bool called = false;
        using var f = new EditorFixture(download: (_, _, _) =>
        {
            called = true;
            return Task.FromResult(new List<DanmakuComment>());
        });
        f.ViewModel.SetTarget(f.Drawable);
        f.ViewModel.Url.Value = "https://example.com/video";

        await f.ViewModel.DownloadAsync();

        Assert.That(called, Is.False);
        Assert.That(f.ViewModel.Status.Value, Does.Contain("対応していない URL"));
    }

    [AvaloniaTest]
    public async Task DownloadErrorsAndEmptyResultsKeepTheComments()
    {
        Exception? error = new CommentDownloadException("ログインが必要です");
        using var f = new EditorFixture(download: (_, _, _) =>
            error != null ? Task.FromException<List<DanmakuComment>>(error) : Task.FromResult(new List<DanmakuComment>()));
        f.ViewModel.SetTarget(f.Drawable);
        f.ViewModel.Url.Value = "https://youtu.be/dQw4w9WgXcQ";

        await f.ViewModel.DownloadAsync();
        Assert.That(f.ViewModel.Status.Value, Is.EqualTo("ログインが必要です"));

        error = new HttpRequestException("timeout");
        await f.ViewModel.DownloadAsync();
        Assert.That(f.ViewModel.Status.Value, Is.EqualTo("取得できませんでした: timeout"));

        error = null;
        await f.ViewModel.DownloadAsync();
        Assert.That(f.ViewModel.Status.Value, Does.Contain("コメントがありませんでした"));

        Assert.That(f.Texts, Has.Length.EqualTo(3));
        Assert.That(f.ViewModel.IsDownloading.Value, Is.False);
    }

    [AvaloniaTest]
    public async Task CancelStopsTheDownload()
    {
        using var f = new EditorFixture(download: async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return [];
        });
        f.ViewModel.SetTarget(f.Drawable);
        f.ViewModel.Url.Value = "https://www.twitch.tv/videos/123";

        Task download = f.ViewModel.DownloadAsync();
        Assert.That(f.ViewModel.IsDownloading.Value, Is.True);
        // A second click while downloading does nothing.
        await f.ViewModel.DownloadAsync();

        f.ViewModel.CancelDownload();
        await download;

        Assert.That(f.ViewModel.Status.Value, Is.EqualTo("中止しました"));
        Assert.That(f.ViewModel.IsDownloading.Value, Is.False);
        Assert.That(f.Texts, Has.Length.EqualTo(3));
    }

    [AvaloniaTest]
    public void TheReplaceSettingIsRemembered()
    {
        using var f = new EditorFixture();
        f.ViewModel.ReplaceOnImport.Value = false;
        var json = new JsonObject();

        f.ViewModel.WriteToJson(json);
        using var other = new EditorFixture();
        other.ViewModel.ReadFromJson(json);

        Assert.That(other.ViewModel.ReplaceOnImport.Value, Is.False);
    }
}

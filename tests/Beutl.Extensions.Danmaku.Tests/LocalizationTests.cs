using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Beutl.Extensions.Danmaku.Comments;
using Beutl.Extensions.Danmaku.Download;
using Beutl.Extensions.Danmaku.Editing;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.Extensions.Danmaku.Properties;
using Beutl.PropertyAdapters;
using LocalizeService = Beutl.Language.LocalizeService;

namespace Beutl.Extensions.Danmaku.Tests;

public class LocalizationTests
{
    private static IEnumerable<CultureInfo> TranslatedCultures => LocalizeService.Instance.SupportedCultures()
        .Where(culture => culture.TwoLetterISOLanguageName != "en");

    [TestCaseSource(nameof(TranslatedCultures))]
    public void TranslationsCoverEveryResourceAndPreserveFormatArguments(CultureInfo culture)
    {
        ResourceSet english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        ResourceSet translation = Strings.ResourceManager.GetResourceSet(culture, true, true)!;
        Assert.That(translation, Is.Not.Null.And.Not.SameAs(english), "Each host language must have its own satellite resources.");
        var source = english.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var translated = translation.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        Assert.That(translated.Keys, Is.EquivalentTo(source.Keys));

        foreach ((string key, string value) in source)
        {
            Assert.That(translated[key], Is.Not.Empty, key);
            string[] Arguments(string text) => Regex.Matches(text, @"\{(\d+)[^{}]*\}")
                .Select(m => m.Groups[1].Value).Order().ToArray();
            Assert.That(Arguments(translated[key]), Is.EqualTo(Arguments(value)), key);
            Assert.DoesNotThrow(() => string.Format(CultureInfo.InvariantCulture, translated[key], 1234, 2345), key);
        }
    }

    [Test]
    public void ResourcesFollowTheUiCultureWithoutCachingLocalizedLabels()
    {
        var drawable = new DanmakuDrawable();
        var adapter = new EnginePropertyAdapter<DanmakuCommentList>(drawable.Comments, drawable);
        DisplayAttribute display = typeof(DanmakuDrawable).GetCustomAttribute<DisplayAttribute>()!;

        // Reuse the same metadata across cultures, including fallback from a language without a translation.
        foreach ((string cultureName, string name, string comments, string position, string white) in new[]
                 {
                     ("ja-JP", "コメント弾幕", "コメント", "流れる", "白"),
                     ("en-US", "Danmaku", "Comments", "Scrolling", "White"),
                     ("zh-CN", "弹幕", "评论", "滚动", "白色"),
                     ("ko-KR", "댓글 탄막", "댓글", "흐름", "흰색"),
                     ("es", "Danmaku", "Comentarios", "Deslizante", "Blanco"),
                     ("es-ES", "Danmaku", "Comentarios", "Deslizante", "Blanco"),
                     ("es-MX", "Danmaku", "Comentarios", "Deslizante", "Blanco"),
                     ("fr-FR", "Danmaku", "Comments", "Scrolling", "White"),
                     ("ja", "コメント弾幕", "コメント", "流れる", "白"),
                 })
        {
            using var culture = new CultureScope(cultureName);
            Assert.That(new DanmakuExtension().DisplayName, Is.EqualTo(name));
            Assert.That(new DanmakuTabExtension().DisplayName, Is.EqualTo(name));
            Assert.That(display.GetName(), Is.EqualTo(name));
            Assert.That(adapter.DisplayName, Is.EqualTo(comments));
            Assert.That(adapter.Description, Is.EqualTo(Strings.CommentsDescription));
            Assert.That(DanmakuTabViewModel.PositionNames[0], Is.EqualTo(position));
            Assert.That(CommentCommands.StandardColors[0].DisplayName, Is.EqualTo(white));
            foreach (PropertyInfo property in typeof(DanmakuDrawable).GetProperties())
            {
                if (property.GetCustomAttribute<DisplayAttribute>() is not { ResourceType: not null } attribute)
                    continue;
                Assert.That(attribute.GetName(), Is.Not.Null.And.Not.Empty, property.Name);
                Assert.DoesNotThrow(() => attribute.GetDescription(), property.Name);
            }
        }
    }

    [TestCase("ja-JP", "対応していない URL です", "yt-dlp が見つかりません")]
    [TestCase("en-US", "This URL is not supported.", "yt-dlp was not found.")]
    [TestCase("zh-CN", "不支持此 URL。", "未找到 yt-dlp。")]
    [TestCase("ko-KR", "지원하지 않는 URL입니다.", "yt-dlp를 찾을 수 없습니다.")]
    [TestCase("es", "Esta URL no es compatible.", "No se encontró yt-dlp.")]
    public async Task DownloaderErrorsUseTheUiLanguage(string uiCulture, string unsupportedUrl, string missingTool)
    {
        using var culture = new CultureScope(uiCulture);
        var downloader = new CommentDownloader(locateYtDlp: () => null);
        try
        {
            await downloader.DownloadAsync("https://example.com/video", null, CancellationToken.None);
            Assert.Fail("The unsupported URL should fail.");
        }
        catch (CommentDownloadException ex)
        {
            Assert.That(ex.Message, Is.EqualTo(unsupportedUrl));
        }

        Assert.That(() => downloader.DownloadAsync("https://www.nicovideo.jp/watch/sm9", null, CancellationToken.None),
            Throws.TypeOf<CommentDownloadException>().With.Message.StartsWith(missingTool));
    }

    [Test]
    public void LocalizedExportHeadersKeepTimesAndCommandsPortable()
    {
        DanmakuComment[] comments =
        [
            new(TimeSpan.FromSeconds(62.5), "日本語 and English", CommentPosition.Top, CommentSize.Big, Beutl.Media.Colors.Red),
        ];
        foreach (CultureInfo uiCulture in LocalizeService.Instance.SupportedCultures())
        {
            // Display language and number formatting can differ in Beutl; file syntax must not follow either.
            using var culture = new CultureScope(uiCulture.Name, "fr-FR");
            string text = CommentWriter.ToText(comments);
            Assert.That(text, Does.StartWith(Strings.ExportHeader));
            Assert.That(text, Does.Contain("1:02.500\tue big #FF0000\t日本語 and English"));
            Assert.That(CommentParser.Parse(text), Is.EqualTo(comments));
            Assert.That(string.Format(Strings.CommentCount, 1234), Does.Contain(1234.ToString("N0")));
        }
    }
}

/// <summary>Restores both thread cultures after a localization test, including tests on Avalonia's UI thread.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;

    public CultureScope(string uiCulture, string? culture = null)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(uiCulture);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture ?? uiCulture);
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.CurrentCulture = _culture;
    }
}

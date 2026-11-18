using Beutl.Services;
using Beutl.Extensions.Danmaku.Graphics;

namespace Beutl.Extensions.Danmaku.Tests;

public class LibraryRegistrationTests
{
    [Test]
    public void LibraryItemCanBeDroppedOnTheTimeline()
    {
        using var library = new LibraryService();
        new DanmakuExtension().Load(TestHost.CreateContext(library));

        // The timeline only accepts library items that carry the EngineObject format; the Drawable format alone
        // shows in the library but is refused on drop.
        Assert.That(library.GetTypesFromFormat(KnownLibraryItemFormats.EngineObject), Does.Contain(typeof(DanmakuDrawable)));
        Assert.That(library.GetTypesFromFormat(KnownLibraryItemFormats.Drawable), Does.Contain(typeof(DanmakuDrawable)));
    }
}

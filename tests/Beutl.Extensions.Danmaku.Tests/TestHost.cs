using Beutl.Configuration;
using Beutl.Extensibility;
using Beutl.Media;
using Beutl.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Beutl.Extensions.Danmaku.Tests;

/// <summary>Loads the extension for the whole run with the host services Beutl gives it.</summary>
[SetUpFixture]
public sealed class TestHost
{
    private LibraryService? _library;
    private DanmakuExtension? _extension;
    private IExtensionContext? _context;

    // The installed fonts, read from the folders Beutl's font settings list by default.
    public static FontManager Fonts { get; } = new(new FontConfig());

    public static IExtensionContext CreateContext(LibraryService library)
    {
        return new ExtensionContext(new ServiceCollection()
            .AddSingleton(Fonts)
            .AddSingleton(library)
            .BuildServiceProvider());
    }

    [OneTimeSetUp]
    public void Load()
    {
        _library = new LibraryService();
        _context = CreateContext(_library);
        _extension = new DanmakuExtension();
        _extension.Load(_context);
    }

    [OneTimeTearDown]
    public void Unload()
    {
        if (_context != null)
            _extension?.Unload(_context);
        _library?.Dispose();
    }
}

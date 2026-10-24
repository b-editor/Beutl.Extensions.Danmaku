using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Beutl.Extensions.Danmaku.Tests;

[assembly: AvaloniaTestApplication(typeof(HeadlessApp))]

namespace Beutl.Extensions.Danmaku.Tests;

/// <summary>A headless Avalonia application for exercising the editors without a display.</summary>
public sealed class HeadlessApp : Application
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

using Beutl.Extensibility;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.Extensions.Danmaku.Properties;
using Beutl.Media;
using Beutl.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Beutl.Extensions.Danmaku;

[Export]
public sealed class DanmakuExtension : Extension
{
    public override string Name => "Danmaku";

    public override string DisplayName => Strings.Danmaku;

    public override void Load(IExtensionContext context)
    {
        base.Load(context);
        DanmakuDrawable.HostFonts = context.Services.GetService<FontManager>();
        context.Services.GetRequiredService<LibraryService>().RegisterGroup(Strings.Danmaku, g => g
            // BindDrawable also binds the EngineObject format, which the timeline requires to accept a drop.
            .AddMultiple(Strings.Danmaku, Strings.DanmakuDescription, m => m.BindDrawable<DanmakuDrawable>()));
    }

    public override void Unload(IExtensionContext context)
    {
        DanmakuDrawable.HostFonts = null;
        base.Unload(context);
    }
}

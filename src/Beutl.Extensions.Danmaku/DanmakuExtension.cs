using Beutl.Extensibility;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.Extensions.Danmaku.Properties;
using Beutl.Services;

namespace Beutl.Extensions.Danmaku;

[Export]
public sealed class DanmakuExtension : Extension
{
    public override string Name => "Danmaku";

    public override string DisplayName => Strings.Danmaku;

    public override void Load()
    {
        base.Load();
        LibraryService.Current.RegisterGroup(Strings.Danmaku, g => g
            // BindDrawable also binds the EngineObject format, which the timeline requires to accept a drop.
            .AddMultiple(Strings.Danmaku, Strings.DanmakuDescription, m => m.BindDrawable<DanmakuDrawable>()));
    }
}

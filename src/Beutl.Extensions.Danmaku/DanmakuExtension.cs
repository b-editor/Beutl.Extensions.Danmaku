using Beutl.Extensibility;
using Beutl.Extensions.Danmaku.Graphics;
using Beutl.Services;

namespace Beutl.Extensions.Danmaku;

[Export]
public sealed class DanmakuExtension : Extension
{
    public override string Name => "Danmaku";

    public override string DisplayName => "コメント弾幕";

    public override void Load()
    {
        base.Load();
        LibraryService.Current.RegisterGroup("コメント弾幕", g => g
            // BindDrawable also binds the EngineObject format, which the timeline requires to accept a drop.
            .AddMultiple("コメント弾幕", "取り込んだコメントやダウンロードしたコメントを画面に流します", m => m.BindDrawable<DanmakuDrawable>()));
    }
}

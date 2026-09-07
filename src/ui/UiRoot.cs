using Godot;

namespace HitboxClone;

/// <summary>
/// The 2D layer the whole UI is drawn into, sitting on top of the 3D viewports.
///
/// It owns nothing but the painter — all state lives in <see cref="Main"/>. Splitting it out of
/// Main is what makes room for splitscreen: the 3D cameras render into SubViewports underneath,
/// and this Control draws menus and every player's HUD over the top of them.
/// </summary>
public partial class UiRoot : Control
{
    UiPainter painter = null!;
    Main app = null!;

    public UiPainter Painter => painter;

    public override void _Ready()
    {
        app = GetParent<Main>();
        painter = new UiPainter(this, ThemeDB.FallbackFont, GetViewportRect().Size);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        painter.Resize(GetViewportRect().Size);
        app.Stack.Draw(painter);
    }
}

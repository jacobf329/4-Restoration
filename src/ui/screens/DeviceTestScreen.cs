using System.Collections.Generic;
using System.Text;

namespace HitboxClone;

/// <summary>
/// Live view of every input source the game can see, with its state as the device layer reports it.
///
/// This exists because "is my pad actually detected, and does the right stick work" is a question
/// couch players ask constantly, and most games make you start a match to find out. It also
/// doubles as the place to verify hot-plug and trigger calibration by hand.
/// </summary>
public sealed class DeviceTestScreen : UiScreen
{
    public override string Title => "CONTROLS & DEVICES";

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices) { }

    public override void Draw(UiPainter p)
    {
        Chrome.Background(p);
        float y = Chrome.Header(p, Title,
            "Press anything — active devices light up. Unplug and replug a pad to check hot-plug.");

        foreach (var d in Devices.All)
        {
            if (d.IsGamepad && !d.Connected) continue;

            var accent = d.AnyActivity ? Pal.Ready : Pal.TextDim;

            p.Panel(40f, y - 8f, p.Size.X - 80f, 62f, Pal.Panel, d.AnyActivity ? Pal.Ready : Pal.PanelHi);
            p.Text($"{d.Label}", 56f, y, 20, accent);
            p.TextRight($"[{d.Kind}]", p.Size.X - 56f, y, 16, Pal.TextDim);
            p.Text(Describe(d), 56f, y + 26f, 15, Pal.TextDim);
            y += 74f;
        }

        if (Devices.ConnectedGamepadCount() == 0)
            p.Text("No gamepad detected. The keyboard schemes above are always available.",
                   48f, y + 8f, 18, Pal.Warn);

        p.HintBar((Glyphs.For(Prompt.Cancel, FirstActive()), "Back"));
    }

    static InputDevice? FirstActive()
    {
        foreach (var d in Devices.All)
            if (d.Connected && d.IsGamepad) return d;
        return Devices.Keyboards[0];
    }

    static string Describe(InputDevice d)
    {
        var sb = new StringBuilder();
        sb.Append($"move ({d.Move.X,5:0.00},{d.Move.Y,5:0.00})   aim ({d.Aim.X,5:0.00},{d.Aim.Y,5:0.00})   nav ({d.NavX,2},{d.NavY,2})   ");
        if (d.AttackHeld) sb.Append("ATTACK ");
        if (d.SpecialHeld) sb.Append("SPECIAL ");
        if (d.DashPressed) sb.Append("DASH ");
        if (d.MeleePressed) sb.Append("MELEE ");
        if (d.UseHeld) sb.Append("USE ");
        if (d.SwapPressed) sb.Append("SWAP ");
        if (d.ConfirmPressed) sb.Append("CONFIRM ");
        if (d.StartPressed) sb.Append("START ");
        if (d.BackPressed) sb.Append("BACK ");

        // Surfaced because it is the tell for a pad running a desktop layout: if pressing a
        // gamepad button lights this row up, something is translating that pad into mouse clicks.
        if (d is KeyboardDevice { Scheme: 0 } kb)
            sb.Append(kb.MouseInUse ? "  [mouse live]" : "  [mouse idle — clicks ignored]");

        return sb.ToString();
    }
}

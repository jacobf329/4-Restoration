using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Gamepad button remapping, itself driven entirely by a gamepad.
///
/// The listening state is the delicate part: a player rebinding **Back** is about to press the very
/// button they would otherwise use to escape. Three separate escape hatches cover that, because a
/// remapping screen that can strand you is worse than no remapping screen at all:
///
///   * capture waits for every button to be released first, so the press that opened the row is
///     never the press that gets captured
///   * listening times out on its own after a few seconds
///   * a keyboard always cancels, and keyboards can always drive menus regardless of whether
///     keyboard *players* are enabled
///
/// On top of that, <see cref="PadBindings"/> guarantees every action keeps at least one binding.
/// </summary>
public sealed class RebindScreen : UiScreen
{
    public override string Title => "CONTROLLER BINDINGS";

    const float ListenTimeout = 6f;

    int cursor;
    PadAction? listening;
    bool awaitingRelease;
    float listenTimer;
    string? note;
    float noteTimer;

    /// <summary>Rows are the actions, then a reset row.</summary>
    int RowCount => PadBindings.Actions.Length + 1;
    int ResetRow => PadBindings.Actions.Length;

    /// <summary>The pad whose vocabulary the labels are drawn in.</summary>
    static InputDevice? FirstPad()
    {
        foreach (var g in Devices.Gamepads) if (g.Connected) return g;
        return null;
    }

    protected override bool OnBack(InputDevice d)
    {
        // While listening, back cancels the capture rather than leaving the screen — but only the
        // pad's own Back button does, not B.
        //
        // Menu cancel is B now, and B is a button someone might reasonably want to bind. If the
        // cancel that leaves a screen also cancelled a capture, B would be the one input on the pad
        // this screen could never assign. Swallowed either way, so B never pops the screen.
        if (listening != null)
        {
            if (d.BackPressed) StopListening("Cancelled");
            return true;
        }

        return base.OnBack(d);
    }

    protected override void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices)
    {
        if (noteTimer > 0f) { noteTimer -= dt; if (noteTimer <= 0f) note = null; }

        if (listening != null) { UpdateListening(dt, devices); return; }

        foreach (var d in devices)
        {
            if (!d.Connected) continue;

            if (d.NavY != 0)
            {
                cursor = Mathf.PosMod(cursor + d.NavY, RowCount);
                Sfx.Play(Sound.MenuMove, -6f);
            }

            if (!d.ConfirmPressed && !d.StartPressed) continue;

            Sfx.Play(Sound.MenuConfirm, -4f);

            if (cursor == ResetRow)
            {
                PadBindings.ResetAll();
                UserSettings.Save();
                Flash("Bindings reset to defaults");
                return;
            }

            StartListening(PadBindings.Actions[cursor]);
            return;
        }
    }

    void StartListening(PadAction a)
    {
        listening = a;
        listenTimer = ListenTimeout;

        // The button that opened this row is still down. Wait for a clean release before
        // capturing anything, or every rebind would instantly capture Attack.
        awaitingRelease = true;
    }

    void StopListening(string? message)
    {
        listening = null;
        awaitingRelease = false;
        if (message != null) Flash(message);
    }

    void UpdateListening(float dt, IReadOnlyList<InputDevice> devices)
    {
        listenTimer -= dt;
        if (listenTimer <= 0f) { StopListening("Timed out — nothing changed"); return; }

        // A keyboard can always bail out, even when keyboard players are disabled.
        foreach (var d in devices)
            if (d is KeyboardDevice && d.BackPressed) { StopListening("Cancelled"); return; }

        bool anyHeld = false;
        foreach (var g in Devices.Gamepads)
            if (g.Connected && g.AnyBindableHeld) { anyHeld = true; break; }

        if (awaitingRelease)
        {
            if (!anyHeld) awaitingRelease = false;
            return;
        }

        foreach (var g in Devices.Gamepads)
        {
            if (!g.Connected) continue;

            if (g.CapturePressed() is not { } captured) continue;

            var action = listening!.Value;
            PadBindings.Rebind(action, captured);
            UserSettings.Save();
            g.Rumble(0.2f, 0.35f, 0.1f);
            Sfx.Play(Sound.MenuConfirm, -3f);
            StopListening($"{PadBindings.DisplayName(action)} set to {captured.Label(g.Kind)}");
            return;
        }
    }

    void Flash(string message) { note = message; noteTimer = 2.6f; }

    public override void Draw(UiPainter p)
    {
        Chrome.Background(p, MenuBackdrop.Options);
        Chrome.Header(p, Title, "Sticks are fixed: left stick moves, right stick looks");

        var pad = FirstPad();
        var kind = pad?.Kind ?? PadKind.Generic;

        float cx = p.Size.X * 0.5f;
        float w = 700f;
        float x = cx - w * 0.5f;
        // Thirteen rows now that Melee has one of its own. At the old 56px pitch the footer landed
        // underneath the hint bar.
        // Fourteen rows now that the class ability has one of its own, plus the reset row. The
        // pitch shrinks as actions are added rather than the list running off the bottom.
        float y = p.Size.Y * 0.15f;
        const float rowH = 47f;

        for (int i = 0; i < PadBindings.Actions.Length; i++)
        {
            var a = PadBindings.Actions[i];
            bool sel = cursor == i && listening == null;
            bool active = listening == a;
            float ry = y + i * rowH;

            if (sel || active)
            {
                p.Panel(x - 14f, ry - 8f, w + 28f, rowH - 6f, Pal.PanelHi, active ? Pal.Warn : Pal.Accent);
                p.Text(">", x - 34f, ry, 26, active ? Pal.Warn : Pal.Accent);
            }

            p.Text(PadBindings.DisplayName(a), x, ry, 26, sel || active ? Pal.Text : Pal.TextDim);

            string value = active
                ? (awaitingRelease ? "release, then press a button…" : "press a button…")
                : PadBindings.Describe(a, kind);

            p.TextRight(value, x + w, ry, active ? 22 : 24,
                        active ? Pal.Warn : PadBindings.IsDefault(a) ? Pal.TextDim : Pal.Accent);
        }

        // Reset row.
        {
            bool sel = cursor == ResetRow && listening == null;
            float ry = y + ResetRow * rowH + 14f;

            if (sel)
            {
                p.Panel(x - 14f, ry - 8f, w + 28f, rowH - 6f, Pal.PanelHi, Pal.Accent);
                p.Text(">", x - 34f, ry, 26, Pal.Accent);
            }

            p.Text("Reset to defaults", x, ry, 26, sel ? Pal.Text : Pal.TextDim);
            p.TextRight(PadBindings.AllDefault() ? "already default" : "", x + w, ry, 20, Pal.TextDim);
        }

        float footY = y + RowCount * rowH + 44f;

        if (note != null)
            p.TextCentered(note, cx, footY, 21, Pal.Ready);
        else if (listening != null)
            p.TextCentered($"Listening… cancels in {listenTimer:0.0}s, or press Backspace",
                           cx, footY, 20, Pal.Warn);
        else if (pad == null)
            p.TextCentered("No gamepad connected — plug one in to rebind", cx, footY, 20, Pal.Warn);
        else
            p.TextCentered("Sticks are fixed: left stick moves, right stick looks.",
                           cx, footY, 19, Pal.TextDim);

        if (listening != null)
            p.HintBar((Glyphs.For(Prompt.Cancel, pad), "Cancel"));
        else
            p.HintBar(
                (Glyphs.For(Prompt.NavVert, pad), "Navigate"),
                (Glyphs.For(Prompt.Confirm, pad), "Rebind"),
                (Glyphs.For(Prompt.Cancel, pad), "Back"));
    }
}

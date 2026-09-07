using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>One row in a <see cref="Menu"/>.</summary>
public sealed class MenuItem
{
    public string Label = "";

    /// <summary>Right-hand value text, for settings rows. Null for plain buttons.</summary>
    public Func<string>? Value;

    /// <summary>Fired on confirm.</summary>
    public Action? Activate;

    /// <summary>Fired on left/right with -1 or +1, for settings rows.</summary>
    public Action<int>? Adjust;

    /// <summary>Greyed out and skipped by the cursor when this returns false.</summary>
    public Func<bool>? Enabled;

    public bool IsEnabled => Enabled?.Invoke() ?? true;
}

/// <summary>
/// A vertical list of items driven by any connected device.
///
/// The cursor is shared rather than per-device: on a menu screen, any player picking up any pad
/// should be able to drive it, and a four-way independent cursor would just mean four people
/// fighting over one selection. The lobby is where per-device state actually belongs, and it
/// tracks that itself.
///
/// Disabled rows are skipped during navigation rather than merely being unselectable, so holding
/// a direction never appears to stall on a row you cannot use.
/// </summary>
public sealed class Menu
{
    public readonly List<MenuItem> Items = new();
    public int Cursor;

    /// <summary>The device that last drove this menu, so hint glyphs match the pad in use.</summary>
    public InputDevice? LastDevice { get; private set; }

    public MenuItem Add(string label, Action activate)
    {
        var it = new MenuItem { Label = label, Activate = activate };
        Items.Add(it);
        return it;
    }

    public MenuItem AddSetting(string label, Func<string> value, Action<int> adjust)
    {
        var it = new MenuItem { Label = label, Value = value, Adjust = adjust };
        Items.Add(it);
        return it;
    }

    public void Update(IReadOnlyList<InputDevice> devices)
    {
        if (Items.Count == 0) return;

        foreach (var d in devices)
        {
            if (!d.Connected) continue;

            if (d.NavY != 0) { Step(d.NavY); LastDevice = d; Sfx.Play(Sound.MenuMove, -6f); }

            if (d.NavX != 0)
            {
                var cur = Current;
                if (cur is { Adjust: not null } && cur.IsEnabled)
                {
                    cur.Adjust(d.NavX);
                    LastDevice = d;
                    Sfx.Play(Sound.MenuMove, -6f);
                }
            }

            if (d.ConfirmPressed || d.StartPressed)
            {
                var cur = Current;
                if (cur is { Activate: not null } && cur.IsEnabled)
                {
                    LastDevice = d;
                    Sfx.Play(Sound.MenuConfirm, -4f);
                    cur.Activate();
                    return;   // the action may have replaced this screen
                }
            }
        }

        EnsureSelectable();
    }

    public MenuItem? Current => Items.Count == 0 ? null : Items[Mathf.Clamp(Cursor, 0, Items.Count - 1)];

    void Step(int dy)
    {
        for (int i = 0; i < Items.Count; i++)
        {
            Cursor = (Cursor + dy + Items.Count) % Items.Count;
            if (Items[Cursor].IsEnabled) return;
        }
    }

    /// <summary>Nudges the cursor off a row that became disabled while it was selected.</summary>
    void EnsureSelectable()
    {
        if (Items.Count == 0) return;
        Cursor = Mathf.Clamp(Cursor, 0, Items.Count - 1);
        if (Items[Cursor].IsEnabled) return;
        Step(1);
    }

    /// <summary>Animated cursor position, in rows. Chases <see cref="Cursor"/>.</summary>
    float visualCursor;

    public void Draw(UiPainter p, float x, float y, float w, int fontSize = 26, float rowH = 52f)
    {
        // The highlight slides to the selected row rather than teleporting. It is a small thing,
        // but a moving bar tells you which direction you just went, which a jump-cut does not.
        float dt = 1f / 60f;
        visualCursor = MathU.Damp(visualCursor, Cursor, 22f, dt);
        if (MathF.Abs(visualCursor - Cursor) < 0.01f) visualCursor = Cursor;

        float hy = y + visualCursor * rowH;
        p.Panel(x - 18f, hy - 9f, w + 36f, rowH - 4f, Pal.PanelHi, Pal.Accent);

        // A solid accent bar down the left edge of the highlight — the strongest single cue for
        // "this row", and it survives being looked at out of the corner of your eye.
        p.Rect(x - 18f, hy - 9f, 5f, rowH - 4f, Pal.Accent);

        for (int i = 0; i < Items.Count; i++)
        {
            var it = Items[i];
            bool sel = i == Cursor;
            bool on = it.IsEnabled;
            float ry = y + i * rowH;

            // A caret as well as the bar: colour alone should not carry the selection.
            if (sel) p.Text(">", x - 40f, ry, fontSize, Pal.Accent);

            Color c = !on ? Pal.TextDim * new Color(1, 1, 1, 0.45f)
                    : sel ? Pal.Text
                          : Pal.TextDim;

            p.Text(it.Label, x, ry, fontSize, c);

            if (it.Value != null)
            {
                string v = it.Value();
                p.TextRight(v, x + w, ry, fontSize, on ? (sel ? Pal.Accent : Pal.TextDim) : c);

                // Only show the adjust arrows on the focused row, so the list stays quiet.
                if (sel && on && it.Adjust != null)
                {
                    p.TextRight("<", x + w - p.Measure(v, fontSize).X - 22f, ry, fontSize, Pal.Accent);
                    p.Text(">", x + w + 14f, ry, fontSize, Pal.Accent);
                }
            }
        }
    }
}

using System.Collections.Generic;

namespace HitboxClone;

/// <summary>
/// Base class for every screen in the game.
///
/// The back behaviour lives here rather than in each screen on purpose. The project's central
/// invariant is that a pad alone can reach and leave every screen, so "back retreats one level"
/// is the inherited default and a screen has to deliberately override it. That makes a dead-end
/// screen something you'd have to write on purpose, instead of something you forget to prevent.
/// </summary>
public abstract class UiScreen
{
    public ScreenStack Stack = null!;

    /// <summary>Shown in the corner so it is always obvious where you are in the menus.</summary>
    public abstract string Title { get; }

    /// <summary>
    /// The label of the row the cursor is on, or null for a screen that is not a menu.
    ///
    /// Exists for the harness. Menu tests used to navigate by counting — "down twice, confirm, and
    /// that is Options" — which is a test of the running order rather than of the thing it claims
    /// to check, and every one of them broke the day a row was added above. Asking for a row by
    /// name is the same test without the brittleness.
    /// </summary>
    public virtual string? SelectedLabel => null;

    public virtual void OnEnter() { }
    public virtual void OnExit() { }

    /// <summary>
    /// The cue this screen sounds like. The menu bed unless a screen says otherwise, which is why
    /// the match screen and the story screen are the only two that override it.
    /// </summary>
    public virtual string MusicCue => "mus_02_standing_orders";

    public void Update(float dt, IReadOnlyList<InputDevice> devices)
    {
        foreach (var d in devices)
        {
            if (!d.Connected || !d.CancelPressed) continue;
            Sfx.Play(Sound.MenuBack, -5f);
            if (OnBack(d)) return;
        }
        UpdateScreen(dt, devices);
    }

    protected abstract void UpdateScreen(float dt, IReadOnlyList<InputDevice> devices);

    public abstract void Draw(UiPainter p);

    /// <summary>
    /// Handle a back press. Returning true means "consumed, stay here". The default retreats one
    /// level, and refuses only at the root so the player is never dumped out of the game by
    /// tapping back one time too many.
    /// </summary>
    protected virtual bool OnBack(InputDevice d)
    {
        if (Stack.Depth > 1) { Stack.Pop(); return true; }
        return false;
    }
}

/// <summary>
/// The screen stack. Deliberately free of any Godot node dependency so the headless test harness
/// can drive the real screens with scripted devices.
/// </summary>
public sealed class ScreenStack
{
    readonly List<UiScreen> stack = new();

    public int Depth => stack.Count;
    public UiScreen Top => stack[^1];
    public bool IsEmpty => stack.Count == 0;

    /// <summary>Set when the root screen asks to leave — the app layer turns this into a quit.</summary>
    public bool QuitRequested { get; set; }

    public void Push(UiScreen s)
    {
        s.Stack = this;
        stack.Add(s);
        s.OnEnter();
    }

    public void Pop()
    {
        if (stack.Count == 0) return;
        var s = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        s.OnExit();
    }

    /// <summary>Replaces the whole stack — used when entering a match from the lobby.</summary>
    public void Reset(UiScreen root)
    {
        while (stack.Count > 0) Pop();
        Push(root);
    }

    public void Update(float dt, IReadOnlyList<InputDevice> devices)
    {
        if (stack.Count == 0) return;

        // The screen on top names the music. Asked here rather than in each screen so that a new
        // screen inherits the menu cue by default instead of inheriting silence - and so a screen
        // only has to say anything at all when it wants something different.
        Music.Play(Top.MusicCue);

        Top.Update(dt, devices);
    }

    public void Draw(UiPainter p)
    {
        if (stack.Count > 0) Top.Draw(p);
    }

    /// <summary>Names of the screens from root to top, for test assertions and debugging.</summary>
    public string[] Trail()
    {
        var names = new string[stack.Count];
        for (int i = 0; i < stack.Count; i++) names[i] = stack[i].GetType().Name;
        return names;
    }
}

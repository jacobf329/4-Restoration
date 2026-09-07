using System;
using System.Collections.Generic;
using Godot;

namespace HitboxClone;

/// <summary>
/// Owns every possible input source and polls them once per frame.
///
/// Hot-plug is handled by diffing the connected-joypad set each poll rather than by wiring the
/// <c>joy_connection_changed</c> signal. That keeps this a plain static class with no Node to
/// keep alive, and it means the headless test harness gets the same code path as the game.
/// </summary>
public static class Devices
{
    public const int MaxGamepads = 8;

    public static readonly List<InputDevice> All = new();
    public static readonly KeyboardDevice[] Keyboards = new KeyboardDevice[2];
    public static readonly GamepadDevice[] Gamepads = new GamepadDevice[MaxGamepads];

    static readonly HashSet<int> connected = new();

    /// <summary>Pads that appeared since the previous poll.</summary>
    public static readonly List<int> JustConnected = new();

    /// <summary>Pads that vanished since the previous poll — drives the mid-match auto-pause.</summary>
    public static readonly List<int> JustDisconnected = new();

    static Devices()
    {
        for (int i = 0; i < 2; i++)
        {
            Keyboards[i] = new KeyboardDevice(i);
            All.Add(Keyboards[i]);
        }
        for (int i = 0; i < MaxGamepads; i++)
        {
            Gamepads[i] = new GamepadDevice(i);
            All.Add(Gamepads[i]);
        }
    }

    public static bool IsPadConnected(int index) => connected.Contains(index);

    public static void PollAll(float dt)
    {
        RefreshConnected();
        foreach (var d in All) d.Poll(dt);
    }

    static void RefreshConnected()
    {
        JustConnected.Clear();
        JustDisconnected.Clear();

        var now = Input.GetConnectedJoypads();

        foreach (int id in now)
            if (id >= 0 && id < MaxGamepads && !connected.Contains(id))
                JustConnected.Add(id);

        foreach (int id in connected)
        {
            bool still = false;
            foreach (int n in now) if (n == id) { still = true; break; }
            if (!still) JustDisconnected.Add(id);
        }

        connected.Clear();
        foreach (int id in now)
            if (id >= 0 && id < MaxGamepads) connected.Add(id);

        // A pad that dropped must not leave buttons latched down.
        foreach (int id in JustDisconnected) Gamepads[id].Clear();
    }

    public static int ConnectedGamepadCount() => connected.Count;

    /// <summary>
    /// Whether a device is allowed to claim a lobby slot. Gamepads always can; keyboards only when
    /// the player has opted in. Menus are unaffected — a keyboard can always navigate the UI, so
    /// someone with no pad connected can still reach the setting that lets them play.
    /// </summary>
    public static bool CanClaimSlot(InputDevice d)
        => d.IsGamepad || UserSettings.KeyboardAndMouse;

    /// <summary>Devices that are usable right now and not already claimed by a player.</summary>
    public static IEnumerable<InputDevice> AvailableUnclaimed(IEnumerable<string> claimedIds)
    {
        var claimed = new HashSet<string>(claimedIds);
        foreach (var d in All)
            if (d.Connected && CanClaimSlot(d) && !claimed.Contains(d.Id))
                yield return d;
    }

    public static InputDevice? ById(string id)
    {
        foreach (var d in All) if (d.Id == id) return d;
        return null;
    }

    /// <summary>
    /// Adds a device to the registry. This is how the headless harness puts a
    /// <see cref="ScriptedDevice"/> in front of the real lobby code — slot claiming looks devices
    /// up through <see cref="ById"/> and <see cref="AvailableUnclaimed"/>, so a test device has to
    /// live here to be seen. Real hardware is registered in the static constructor instead.
    /// </summary>
    public static void Register(InputDevice d)
    {
        if (!All.Contains(d)) All.Add(d);
    }

    public static void Unregister(InputDevice d) => All.Remove(d);
}

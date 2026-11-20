using HHT.Bridge.Core.Ch9329;
using HHT.Bridge.Core.Hid;

namespace HHT.Bridge.Core.Input;

public enum HostCommand
{
    None,
    ReleaseCapture,
    ToggleFullscreen,
    Screenshot,
    SendCtrlAltDel,
}

/// <summary>
/// Turns raw key events from the technician laptop into keyboard reports for the target,
/// while reserving <b>Right Ctrl</b> as the "host key" (like VirtualBox):
///   • tap Right Ctrl alone            → release keyboard/mouse capture
///   • Right Ctrl + F                  → toggle full screen
///   • Right Ctrl + S                  → screenshot
///   • Right Ctrl + Del (or End)       → send Ctrl+Alt+Del to the target
/// Right Ctrl itself is never forwarded; use Left Ctrl on the target.
/// </summary>
public sealed class HostKeyRouter
{
    public const int VkRightCtrl = 0xA3;
    private const int VkF = 0x46, VkS = 0x53, VkDelete = 0x2E, VkEnd = 0x23;

    private readonly KeyboardState _state = new();
    private bool _hostKeyDown;
    private bool _hostKeyUsedInCombo;

    public KeyboardState State => _state;

    public readonly record struct Outcome(KeyboardReport? Report, HostCommand Command, bool Unmapped)
    {
        public static readonly Outcome Nothing = new(null, HostCommand.None, false);
    }

    /// <summary>Process one key event. Auto-repeat key-downs produce no report.</summary>
    public Outcome OnKey(int vk, bool extended, bool down)
    {
        // Normalise generic Ctrl with extended flag to Right Ctrl.
        bool isHostKey = vk == VkRightCtrl || (vk == 0x11 && extended);
        if (isHostKey)
        {
            if (down)
            {
                if (!_hostKeyDown) { _hostKeyDown = true; _hostKeyUsedInCombo = false; }
                return Outcome.Nothing;
            }
            bool tap = _hostKeyDown && !_hostKeyUsedInCombo;
            _hostKeyDown = false;
            return tap ? new Outcome(null, HostCommand.ReleaseCapture, false) : Outcome.Nothing;
        }

        if (_hostKeyDown)
        {
            if (!down) return Outcome.Nothing; // swallow releases of combo keys
            _hostKeyUsedInCombo = true;
            return vk switch
            {
                VkF => new Outcome(null, HostCommand.ToggleFullscreen, false),
                VkS => new Outcome(null, HostCommand.Screenshot, false),
                VkDelete or VkEnd => new Outcome(null, HostCommand.SendCtrlAltDel, false),
                _ => Outcome.Nothing,
            };
        }

        if (!VirtualKeyMap.TryMap(vk, extended, out var usage)) return new Outcome(null, HostCommand.None, true);
        bool changed = down ? _state.Press(usage) : _state.Release(usage);
        return changed ? new Outcome(_state.ToReport(), HostCommand.None, false) : Outcome.Nothing;
    }

    /// <summary>Call when capture ends or focus is lost so no key stays stuck down on the target.</summary>
    public KeyboardReport? ReleaseAll()
    {
        _hostKeyDown = false;
        return _state.ReleaseAll() ? KeyboardReport.Empty : null;
    }
}

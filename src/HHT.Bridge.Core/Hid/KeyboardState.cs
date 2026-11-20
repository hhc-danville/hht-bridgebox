using HHT.Bridge.Core.Ch9329;

namespace HHT.Bridge.Core.Hid;

/// <summary>
/// Tracks which keys are held on the virtual keyboard and produces boot-protocol reports.
/// Six-key rollover: a seventh simultaneous non-modifier key is ignored until one is released.
/// Not thread-safe; call from one thread (the UI/hook thread).
/// </summary>
public sealed class KeyboardState
{
    private readonly List<byte> _keys = new(6);
    private KeyModifiers _modifiers;

    public KeyModifiers Modifiers => _modifiers;
    public IReadOnlyList<byte> PressedKeys => _keys;
    public bool AnythingPressed => _modifiers != KeyModifiers.None || _keys.Count > 0;

    /// <returns>True if the report changed and must be sent.</returns>
    public bool Press(byte usage)
    {
        if (usage == 0) return false;
        if (HidUsage.IsModifier(usage))
        {
            var bit = HidUsage.ModifierBit(usage);
            if ((_modifiers & bit) != 0) return false;
            _modifiers |= bit;
            return true;
        }
        if (_keys.Contains(usage) || _keys.Count >= 6) return false;
        _keys.Add(usage);
        return true;
    }

    /// <returns>True if the report changed and must be sent.</returns>
    public bool Release(byte usage)
    {
        if (HidUsage.IsModifier(usage))
        {
            var bit = HidUsage.ModifierBit(usage);
            if ((_modifiers & bit) == 0) return false;
            _modifiers &= ~bit;
            return true;
        }
        return _keys.Remove(usage);
    }

    /// <returns>True if anything was held (a release-all report must be sent).</returns>
    public bool ReleaseAll()
    {
        bool had = AnythingPressed;
        _keys.Clear();
        _modifiers = KeyModifiers.None;
        return had;
    }

    public KeyboardReport ToReport() => new(_modifiers, _keys.ToArray());
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HHT.Bridge.TechConsole.Input;

/// <summary>
/// WH_KEYBOARD_LL hook (documented Win32 API) so keys the local OS would normally act on —
/// Win, Alt+Tab, Alt+F4, Ctrl+Esc — are sent to the target instead.
///
/// It is installed ONLY while the technician has explicitly captured input by clicking the
/// video, and removed as soon as capture ends or the window loses focus.
/// Ctrl+Alt+Del and Win+L cannot be intercepted by any user-mode app; the console offers
/// buttons/hotkeys for those instead.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    /// <summary>Return true to swallow the key so the local PC never sees it.</summary>
    public delegate bool KeyHandler(int virtualKey, int scanCode, bool extended, bool down);

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;
    private const uint LlkhfExtended = 0x01, LlkhfInjected = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly HookProc _proc; // keep the delegate alive while hooked
    private readonly KeyHandler _handler;
    private IntPtr _hook = IntPtr.Zero;

    public LowLevelKeyboardHook(KeyHandler handler)
    {
        _handler = handler;
        _proc = Callback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>Must be called on a thread with a message loop (the WPF UI thread).</summary>
    public void Install()
    {
        if (IsInstalled) return;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx failed");
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is WmKeyDown or WmSysKeyDown;
            bool up = msg is WmKeyUp or WmSysKeyUp;
            // Ignore synthetic input (ours or another app's) so it can't be relayed to the target.
            if ((down || up) && (k.Flags & LlkhfInjected) == 0)
            {
                bool swallow;
                try { swallow = _handler((int)k.VkCode, (int)k.ScanCode, (k.Flags & LlkhfExtended) != 0, down); }
                catch { swallow = false; }
                if (swallow) return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}

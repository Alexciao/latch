using System.Runtime.InteropServices;
using System.Windows.Interop;
using Latch.Core;

namespace Latch.Host;

/// <summary>The tray icon and its menu (Open Latch, Lock all now, Restart to update when one is ready, Quit), over
/// Shell_NotifyIcon. One state, no balloons (Latch's own notification card says when it is running).</summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = Native.WM_APP + 1;
    private const int CmdOpen = 1, CmdLockAll = 2, CmdQuit = 3, CmdUpdate = 4;
    private readonly HwndSource _window;
    private readonly uint _taskbarCreated;
    private readonly Action _open, _lockAll, _quit, _update;
    private nint _icon;

    /// <summary>Set once an update has been downloaded: the menu then offers to restart into it.</summary>
    public bool UpdateReady { get; set; }

    public TrayIcon(Action open, Action lockAll, Action quit, Action update)
    {
        _update = update;
        _open = open;
        _lockAll = lockAll;
        _quit = quit;
        // A hidden top-level window (not message-only) so the popup menu can take the foreground and dismiss itself.
        _window = new HwndSource(new HwndSourceParameters("Latch tray") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        _window.AddHook(WndProc);
        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        Theme.AllowDarkMenus();
        Add();
    }

    private Native.NOTIFYICONDATAW Data(uint flags)
    {
        if (_icon == 0)
        {
            var size = Native.GetSystemMetrics(49); // SM_CXSMICON
            _icon = Native.LoadImageW(0, Path.Combine(AppContext.BaseDirectory, "Latch.ico"), 1, size, size, 0x10); // IMAGE_ICON, LR_LOADFROMFILE
        }
        return new Native.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = Copy.Get("tray.tooltip"),
            szInfo = "",
            szInfoTitle = "",
            uVersion = 4,
        };
    }

    private void Add()
    {
        var data = Data(Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        Native.Shell_NotifyIconW(Native.NIM_ADD, ref data);
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref data);
    }

    private const int HotkeyId = 1;
    private const uint ModWin = 0x8, ModShift = 0x4, ModNoRepeat = 0x4000, VkX = 0x58;
    private bool _hotkey;

    /// <summary>
    /// Win+Shift+X locks everything. RegisterHotKey, not a keyboard hook: Windows delivers only this one combination,
    /// nothing else is seen, and turning it off unregisters it. Returns false if another app already owns it.
    /// </summary>
    public bool SetHotkey(bool on)
    {
        if (_hotkey) Native.UnregisterHotKey(_window.Handle, HotkeyId);
        _hotkey = on && Native.RegisterHotKey(_window.Handle, HotkeyId, ModWin | ModShift | ModNoRepeat, VkX);
        return !on || _hotkey;
    }

    public event Action? HotkeyPressed;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam == HotkeyId) // WM_HOTKEY
        {
            HotkeyPressed?.Invoke();
            handled = true;
            return 0;
        }
        if (msg == _taskbarCreated)
        {
            Add(); // Explorer restarted.
            return 0;
        }
        if (msg != CallbackMessage) return 0;
        var ev = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (ev)
        {
            case Native.NIN_SELECT:
            case Native.NIN_KEYSELECT:
            case Native.WM_LBUTTONUP:
                _open();
                break;
            case Native.WM_CONTEXTMENU:
            case Native.WM_RBUTTONUP:
                ShowMenu();
                break;
        }
        handled = true;
        return 0;
    }

    private void ShowMenu()
    {
        var menu = Native.CreatePopupMenu();
        Native.AppendMenuW(menu, 0, CmdOpen, Copy.Get("tray.open"));
        Native.AppendMenuW(menu, 0, CmdLockAll, Copy.Get("tray.lockAll"));
        Native.AppendMenuW(menu, 0x800, 0, null); // MF_SEPARATOR
        if (UpdateReady) Native.AppendMenuW(menu, 0, CmdUpdate, Copy.Get("tray.restartToUpdate"));
        Native.AppendMenuW(menu, 0, CmdQuit, Copy.Get("tray.quit"));
        Native.GetCursorPos(out var pt);
        Native.SetForegroundWindow(_window.Handle);
        var cmd = Native.TrackPopupMenuEx(menu, 0x0100 | 0x0020, pt.X, pt.Y, _window.Handle, 0); // TPM_RETURNCMD | TPM_RIGHTBUTTON
        Native.DestroyMenu(menu);
        switch (cmd)
        {
            case CmdOpen: _open(); break;
            case CmdLockAll: _lockAll(); break;
            case CmdQuit: _quit(); break;
            case CmdUpdate: _update(); break;
        }
    }

    public void Dispose()
    {
        if (_hotkey) Native.UnregisterHotKey(_window.Handle, HotkeyId);
        var data = Data(0);
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref data);
        if (_icon != 0) Native.DestroyIcon(_icon);
        _window.Dispose();
    }
}

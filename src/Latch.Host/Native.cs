using System.Runtime.InteropServices;
using System.Text;

namespace Latch.Host;

/// <summary>Win32 and shell interop. Only declarations live here; behaviour lives in the classes that use them.</summary>
internal static class Native
{
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    public const int OBJID_WINDOW = 0;
    public const uint GA_ROOT = 2;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_APP = 0x8000;
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint NIN_SELECT = 0x0400;
    public const uint NIN_KEYSELECT = 0x0401;

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint SYNCHRONIZE = 0x00100000;

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMWA_CLOAK = 13;
    public const int DWMWA_CLOAKED = 14;

    public delegate void WinEventProc(nint hook, uint ev, nint hwnd, int idObject, int idChild, uint thread, uint time);
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] public static extern nint SetWinEventHook(uint min, uint max, nint mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern nint DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessageW(uint thread, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(nint hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(nint parent, EnumWindowsProc proc, nint lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, nint extra);
    [DllImport("user32.dll")] public static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(nint menu, uint flags, nint id, string? text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint LoadImageW(nint inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern uint GetTickCount();
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(nint process, nint min, nint max);
    [DllImport("kernel32.dll")] public static extern nint OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(nint h, uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(nint h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetPackageFamilyName(nint h, ref int len, StringBuilder? sb);
    [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(nint h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetRect(nint hwnd, int attr, out RECT value, int size);
    public static int DwmGetWindowRect(nint hwnd, out RECT r) => DwmGetRect(hwnd, 9, out r, Marshal.SizeOf<RECT>()); // DWMWA_EXTENDED_FRAME_BOUNDS
    [DllImport("user32.dll")] public static extern nint GetWindow(nint hwnd, uint cmd);

    public delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PAINTSTRUCT { public nint hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern nint CreateWindowExW(int exStyle, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] public static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern nint BeginPaint(nint hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern bool EndPaint(nint hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] public static extern int FillRect(nint hdc, ref RECT rc, nint brush);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int cx, int cy, uint step, nint brush, uint flags);
    [DllImport("user32.dll")] public static extern nint SetTimer(nint hwnd, nint id, uint ms, nint proc);
    [DllImport("user32.dll")] public static extern bool KillTimer(nint hwnd, nint id);
    [DllImport("user32.dll")] public static extern nint LoadCursorW(nint inst, int id);
    [DllImport("gdi32.dll")] public static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(nint hwnd, ref POINT pt);
    [DllImport("user32.dll")] public static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public nint hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("gdi32.dll")] public static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] public static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint obj);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] public static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int DrawTextW(nint hdc, string text, int count, ref RECT rect, uint format);
    [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out nint bits, nint section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("msimg32.dll")] public static extern bool AlphaBlend(nint dest, int dx, int dy, int dw, int dh, nint src, int sx, int sy, int sw, int sh, BLENDFUNCTION blend);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] public static extern nint GetClassLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] public static extern nint SetClassLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] public static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hwnd, int id);
    public const uint EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_CLOAKED = 0x8017;

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    public static readonly nint HWND_TOPMOST = -1;
    public static readonly nint HWND_NOTOPMOST = -2;
    public static readonly nint HWND_TOP = 0;
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    // ---- Tray -----------------------------------------------------------------------------------------------------

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATAW
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);

    // ---- Shell and property stores --------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(string guid, uint id) { fmtid = new Guid(guid); pid = id; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public nint p; }

    public static readonly PROPERTYKEY PKEY_AppUserModel_ID = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 5);
    public static readonly PROPERTYKEY PKEY_Link_TargetParsingPath = new("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25", 2);
    public static readonly PROPERTYKEY PKEY_AppUserModel_PackageFamilyName = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3", 17);

    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        void Commit();
    }

    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(nint hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    public const uint SIGDN_NORMALDISPLAY = 0;
    public const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem2
    {
        void BindToHandler(nint pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
        void GetPropertyStore(int flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetPropertyStoreWithCreateObject(int flags, [MarshalAs(UnmanagedType.IUnknown)] object punk, ref Guid riid, out nint ppv);
        void GetPropertyStoreForKeys(nint rgKeys, uint cKeys, int flags, ref Guid riid, out nint ppv);
        void GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out nint ppv);
        void Update(nint pbc);
        void GetProperty(ref PROPERTYKEY key, out PROPVARIANT ppropvar);
        void GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
        void GetFileTime(ref PROPERTYKEY key, out long pft);
        void GetInt32(ref PROPERTYKEY key, out int pi);
        [PreserveSig] int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem rgelt, out uint fetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems ppenum);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out nint phbm);
    }

    public static readonly Guid FOLDERID_AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    public static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderItem(ref Guid rfid, uint flags, nint token, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(string path, nint pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll")]
    public static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, nint token, out nint path);

    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(nint p);

    // ---- GDI ------------------------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public nint bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")] public static extern int GetObjectW(nint h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(nint hdc, nint hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint h);
    [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint hdc);
}

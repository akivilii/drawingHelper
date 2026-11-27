using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DrawingHelper.App.Interop;

/// <summary>Optional Win32 global hotkeys so users can step tiles/colors while Minecraft has focus.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkLeft = 0x25;
    private const uint VkUp = 0x26;
    private const uint VkRight = 0x27;
    private const uint VkDown = 0x28;
    private const uint VkQ = 0x51;
    private const uint VkE = 0x45;

    private const int IdLeft = 9001;
    private const int IdRight = 9002;
    private const int IdUp = 9003;
    private const int IdDown = 9004;
    private const int IdPrevColor = 9005;
    private const int IdNextColor = 9006;

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;

    public event Action<int, int>? TileDirectionRequested;
    public event Action<int>? ColorStepRequested;

    public bool IsEnabled => _registered;

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);
    }

    public bool SetEnabled(bool enable)
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        if (enable && !_registered)
        {
            uint mods = ModControl | ModAlt | ModNoRepeat;
            bool ok = true;
            ok &= RegisterHotKey(_hwnd, IdLeft, mods, VkLeft);
            ok &= RegisterHotKey(_hwnd, IdRight, mods, VkRight);
            ok &= RegisterHotKey(_hwnd, IdUp, mods, VkUp);
            ok &= RegisterHotKey(_hwnd, IdDown, mods, VkDown);
            ok &= RegisterHotKey(_hwnd, IdPrevColor, mods, VkQ);
            ok &= RegisterHotKey(_hwnd, IdNextColor, mods, VkE);
            _registered = true;
            return ok;
        }

        if (!enable && _registered)
        {
            UnregisterAll();
        }
        return true;
    }

    private void UnregisterAll()
    {
        if (_hwnd == IntPtr.Zero || !_registered)
            return;
        UnregisterHotKey(_hwnd, IdLeft);
        UnregisterHotKey(_hwnd, IdRight);
        UnregisterHotKey(_hwnd, IdUp);
        UnregisterHotKey(_hwnd, IdDown);
        UnregisterHotKey(_hwnd, IdPrevColor);
        UnregisterHotKey(_hwnd, IdNextColor);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            switch (id)
            {
                case IdLeft: TileDirectionRequested?.Invoke(-1, 0); handled = true; break;
                case IdRight: TileDirectionRequested?.Invoke(1, 0); handled = true; break;
                case IdUp: TileDirectionRequested?.Invoke(0, -1); handled = true; break;
                case IdDown: TileDirectionRequested?.Invoke(0, 1); handled = true; break;
                case IdPrevColor: ColorStepRequested?.Invoke(-1); handled = true; break;
                case IdNextColor: ColorStepRequested?.Invoke(1); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}

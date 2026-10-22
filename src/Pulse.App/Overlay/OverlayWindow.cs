using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Pulse.App.Native;
using Pulse.Core.Settings;

namespace Pulse.App.Overlay;

/// <summary>
/// A separate, click-through, never-focused, topmost window drawn over the game. Nothing is injected into the game:
/// the game only sees another window on the desktop, like a chat popup. Visible over borderless and
/// "fullscreen optimized" games; legacy exclusive fullscreen hides all other windows, so it won't show there.
/// The window is sized to its content because layered-window cost grows with window area.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private nint _hwnd;
    private Win32.RECT _lastPlacement;

    public OverlayWindow()
    {
        Title = "Pulse Overlay";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        // Sized explicitly from the visual: SizeToContent stops tracking once the window has been positioned via SetWindowPos.
        SizeToContent = SizeToContent.Manual;
        Focusable = false;
        IsHitTestVisible = false;
        Content = Visual;
        Visual.ContentSizeChanged += FitToContent;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => Reposition(force: true);
    }

    public OverlayVisual Visual { get; } = new();

    public OverlayCorner Corner { get; set; } = OverlayCorner.TopLeft;
    public int OffsetX { get; set; } = 16;
    public int OffsetY { get; set; } = 16;

    /// <summary>The game window whose monitor the overlay sits on; 0 = primary monitor.</summary>
    public nint AnchorHwnd { get; set; }

    public void FitToContent()
    {
        Size size = Visual.ContentSize;
        Width = Math.Ceiling(size.Width * Visual.Scale);
        Height = Math.Ceiling(size.Height * Visual.Scale);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        nint ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_LAYERED;
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, ex);
    }

    /// <summary>Moves the overlay to the configured corner of the anchor's monitor (physical pixels, DPI-aware).</summary>
    public void Reposition(bool force = false)
    {
        if (_hwnd == 0)
            return;
        nint anchor = AnchorHwnd != 0 && Win32.IsWindow(AnchorHwnd) ? AnchorHwnd : 0;
        if (!Win32.TryGetMonitorRect(anchor, out Win32.RECT m) || !Win32.GetWindowRect(_hwnd, out Win32.RECT self))
            return;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int ox = (int)Math.Round(OffsetX * dpi.DpiScaleX);
        int oy = (int)Math.Round(OffsetY * dpi.DpiScaleY);
        int x = Corner switch
        {
            OverlayCorner.TopRight or OverlayCorner.BottomRight => m.Right - self.Width - ox,
            OverlayCorner.TopCenter => m.Left + (m.Width - self.Width) / 2,
            _ => m.Left + ox,
        };
        int y = Corner is OverlayCorner.BottomLeft or OverlayCorner.BottomRight ? m.Bottom - self.Height - oy : m.Top + oy;

        var target = new Win32.RECT { Left = x, Top = y, Right = x + self.Width, Bottom = y + self.Height };
        if (!force && target.Left == _lastPlacement.Left && target.Top == _lastPlacement.Top && target.Right == _lastPlacement.Right)
            return;
        _lastPlacement = target;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    /// <summary>Games and other topmost windows can push us down the z-order; put the overlay back on top.</summary>
    public void ReassertTopmost()
    {
        if (_hwnd != 0)
            Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }
}

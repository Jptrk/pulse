using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Pulse.Core.Settings;

namespace Pulse.App.UI;

/// <summary>Tray icon with live temps in the tooltip and quick overlay controls.</summary>
internal sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Dictionary<OverlayMode, MenuItem> _modeItems = [];
    private readonly MenuItem _hideItem;

    public TrayService(Action open, Action<OverlayMode> setMode, Action toggleHidden, Action exit)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open Pulse", open, bold: true));
        menu.Items.Add(new Separator());
        foreach (OverlayMode mode in new[] { OverlayMode.Strip, OverlayMode.Panel, OverlayMode.Detailed, OverlayMode.Off })
        {
            var item = Item(mode == OverlayMode.Off ? "Overlay off" : $"Overlay: {mode}", () => setMode(mode));
            item.IsCheckable = true;
            _modeItems[mode] = item;
            menu.Items.Add(item);
        }
        _hideItem = Item("Hide overlay for now", toggleHidden);
        _hideItem.IsCheckable = true;
        menu.Items.Add(_hideItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", exit));

        _icon = new TaskbarIcon
        {
            ToolTipText = "Pulse",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/pulse.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => open();
        // Efficiency mode would put Pulse on EcoQoS and starve the overlay while a game runs.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    public void SetOverlayState(OverlayMode mode, bool hidden)
    {
        foreach (var (m, item) in _modeItems)
            item.IsChecked = m == mode;
        _hideItem.IsChecked = hidden;
    }

    public void SetTooltip(string text) => _icon.ToolTipText = text.Length > 120 ? text[..120] : text;

    public void Notify(string title, string message) => _icon.ShowNotification(title, message, NotificationIcon.Info);

    private static MenuItem Item(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header };
        if (bold)
            item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose() => _icon.Dispose();
}

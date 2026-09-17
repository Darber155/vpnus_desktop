using System.Windows;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VpnUs.App.ViewModels;

namespace VpnUs.App.Services;

/// <summary>Иконка в трее (рисуется программно) с контекстным меню.</summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly MainViewModel _viewModel;
    private readonly Window _window;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayIconManager(MainViewModel viewModel, Window window)
    {
        _viewModel = viewModel;
        _window = window;
        _icon = CreateIcon();

        _toggleItem = new ToolStripMenuItem("Подключить");
        _toggleItem.Click += async (_, _) => await _viewModel.Home.ToggleConnectionAsync();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть VpnUs", null, (_, _) => App.Instance.ToggleMainWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleItem);
        menu.Items.Add("Обновить подписку", null, async (_, _) => await _viewModel.RefreshSubscriptionAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => App.Instance.ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "VpnUs",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => App.Instance.ToggleMainWindow();
        _viewModel.Home.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HomeViewModel.IsConnected) or nameof(HomeViewModel.StatusLabel))
            {
                UpdateMenu();
            }
        };

        UpdateMenu();
    }

    public void Notify(string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = "VpnUs";
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(2500);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void UpdateMenu()
    {
        var connected = _viewModel.Home.IsConnected;
        _toggleItem.Text = connected ? "Отключить" : "Подключить";
        _notifyIcon.Text = connected
            ? Truncate("VpnUs: подключено — " + _viewModel.Home.ServerName)
            : "VpnUs: отключено";
    }

    private static string Truncate(string text) => text.Length <= 63 ? text : text[..60] + "...";

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, 32, 32),
            Color.FromArgb(91, 140, 255),
            Color.FromArgb(124, 92, 255),
            45f);

        graphics.FillEllipse(brush, 1, 1, 30, 30);

        using var font = new Font("Segoe UI", 15, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("V", font, Brushes.White, new RectangleF(0, 0, 32, 32), format);

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}

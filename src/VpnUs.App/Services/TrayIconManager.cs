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
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _serverItem;
    private readonly MainViewModel _viewModel;
    private readonly Icon _icon;
    private bool _disposed;

    public TrayIconManager(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        _icon = CreateIcon();

        _statusItem = new ToolStripMenuItem("VpnUs · отключено") { Enabled = false };
        _toggleItem = new ToolStripMenuItem("Подключить");
        _toggleItem.Click += async (_, _) => await _viewModel.Home.ToggleConnectionAsync();
        _serverItem = new ToolStripMenuItem("Сервер: —");
        _serverItem.Click += (_, _) => OpenPage(_viewModel.Servers);

        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 9F) };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть VpnUs", null, (_, _) => App.Instance.ShowMainWindow());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(_serverItem);
        menu.Items.Add("Приложения", null, (_, _) => OpenPage(_viewModel.Apps));
        menu.Items.Add("Обновить подписку", null, async (_, _) => await _viewModel.RefreshSubscriptionAsync());
        menu.Items.Add("Логи", null, (_, _) => OpenPage(_viewModel.Logs));
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
            if (e.PropertyName is nameof(HomeViewModel.IsConnected)
                or nameof(HomeViewModel.StatusLabel)
                or nameof(HomeViewModel.ServerName))
            {
                UpdateMenu();
            }
        };

        UpdateMenu();
    }

    private void OpenPage(object page)
    {
        App.Instance.ShowMainWindow();
        _viewModel.NavigateTo(page);
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
        var server = string.IsNullOrWhiteSpace(_viewModel.Home.ServerName) ? "—" : _viewModel.Home.ServerName;

        _statusItem.Text = connected ? "VpnUs · подключено" : "VpnUs · отключено";
        _toggleItem.Text = connected ? "Отключить" : "Подключить";
        _serverItem.Text = "Сервер: " + Truncate(server, 40);

        _notifyIcon.Text = connected
            ? Truncate($"VpnUs: подключено — {server}", 120)
            : "VpnUs: отключено";
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..(max - 3)] + "...";

    private static Icon CreateIcon()
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (System.IO.File.Exists(iconPath))
        {
            try
            {
                return new Icon(iconPath, 32, 32);
            }
            catch
            {
            }
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) && System.IO.File.Exists(processPath))
        {
            try
            {
                var exeIcon = Icon.ExtractAssociatedIcon(processPath);
                if (exeIcon is not null)
                {
                    return new Icon(exeIcon, 32, 32);
                }
            }
            catch
            {
            }
        }

        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, 32, 32),
            Color.FromArgb(107, 140, 255),
            Color.FromArgb(154, 107, 255),
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

using System.Windows;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using VpnUs.Core.Storage;

namespace VpnUs.App.Services;

public static class UiLog
{
    private static readonly object Gate = new();

    public static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                VpnUsPaths.EnsureUserData();
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(VpnUsPaths.UiLogFile, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Mica/Acrylic и тёмный заголовок окна на Windows 11.</summary>
public static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtMainWindow = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);

    public static bool TryApply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var dark = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            var corners = 2;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref corners, sizeof(int));

            if (DwmIsCompositionEnabled(out var composition) != 0 || !composition)
            {
                return false;
            }

            var backdrop = DwmsbtMainWindow;
            return DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var mica = TryApply(hwnd);

        if (mica && PresentationSource.FromVisual(window) is HwndSource source && source.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
            window.Background = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            window.Background = (System.Windows.Media.Brush)window.FindResource("BgBrush");
        }
    }
}

public static class AdminLauncher
{
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException)
            {
                return false;
            }
        }
    }

    public static string ServiceExePath
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "VpnUs.Service.exe"),
                Path.Combine(AppContext.BaseDirectory, "service", "VpnUs.Service.exe"),
            };

            return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
        }
    }

    public static (bool Started, string? Error) RunServiceCommand(string command)
    {
        var exe = ServiceExePath;
        if (!File.Exists(exe))
        {
            return (false, $"Не найден VpnUs.Service.exe рядом с приложением ({AppContext.BaseDirectory})");
        }

        try
        {
            var psi = new ProcessStartInfo(exe, command)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };

            Process.Start(psi);
            return (true, null);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, "Запрос прав администратора отклонён: " + ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }
    }
}

public static class AppAutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VpnUs";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath ?? "";
                key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            UiLog.Write("warn", "Автозапуск: " + ex.Message);
        }
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Serilog;

namespace KeyPulse.Views;

/// <summary>Lets the native title bar follow the same dynamic theme resource as the window.</summary>
public static class WindowTheme
{
    public static readonly DependencyProperty IsDarkProperty = DependencyProperty.RegisterAttached(
        "IsDark",
        typeof(bool),
        typeof(WindowTheme),
        new PropertyMetadata(false, OnIsDarkChanged)
    );

    public static bool GetIsDark(DependencyObject target) => (bool)target.GetValue(IsDarkProperty);

    public static void SetIsDark(DependencyObject target, bool value) => target.SetValue(IsDarkProperty, value);

    private static void OnIsDarkChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Window window)
            return;
        if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            window.SourceInitialized -= OnSourceInitialized;
            window.SourceInitialized += OnSourceInitialized;
        }
        else
            Apply(window);
    }

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        var window = (Window)sender!;
        window.SourceInitialized -= OnSourceInitialized;
        Apply(window);
    }

    private static void Apply(Window window)
    {
        // Unsupported Windows versions retain their native title bar.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;
        var dark = GetIsDark(window) ? 1 : 0;
        var result = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int));
        if (result < 0)
            Log.Debug("Window title bar appearance could not be updated: {Result}", result);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}

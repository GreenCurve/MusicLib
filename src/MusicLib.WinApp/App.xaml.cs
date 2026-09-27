using Microsoft.UI.Xaml;

namespace MusicLib.WinApp;

public partial class App : Application
{
    // Pages need the Window's HWND for file pickers (unpackaged apps have no
    // implicit "current window" the way packaged apps do).
    public static Window? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
        LibVLCSharp.Shared.Core.Initialize();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}

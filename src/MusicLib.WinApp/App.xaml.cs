using Microsoft.UI.Xaml;

namespace MusicLib.WinApp;

public partial class App : Application
{
    // Pages need the Window's HWND for file pickers (unpackaged apps have no
    // implicit "current window" the way packaged apps do).
    public static Window? MainWindowInstance { get; private set; }
    public static (MusicLib.Core.Track Track, string DataDir, string HomePath)? PendingPlayerContext { get; set; }
    public App()
    {
        InitializeComponent();
        var nativeDir = System.IO.Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        LibVLCSharp.Shared.Core.Initialize(nativeDir);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}

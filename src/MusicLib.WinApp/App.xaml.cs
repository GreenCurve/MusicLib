using Microsoft.UI.Xaml;

namespace MusicLib.WinApp;

public partial class App : Application
{
    // Pages need the Window's HWND for file pickers (unpackaged apps have no
    // implicit "current window" the way packaged apps do).
    public static Window? MainWindowInstance { get; private set; }

    // Frame.Navigate's parameter crosses into WinUI's native navigation machinery, which
    // isn't built to handle an arbitrary CLR ValueTuple — passing one there can crash the
    // native side outright instead of throwing a normal .NET exception. A plain static field
    // never crosses that boundary, so it sidesteps the problem entirely.
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

using Microsoft.UI.Xaml;

namespace MusicLib.WinApp;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "MusicLib";
        RootFrame.Navigate(typeof(LibraryPage));
    }
}

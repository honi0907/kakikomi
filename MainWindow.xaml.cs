using Microsoft.UI.Xaml;
using System.IO;

namespace Kakikomi;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(icon))
                AppWindow.SetIcon(icon);
        }
        catch
        {
            // ignore icon failures
        }

        RootFrame.Navigate(typeof(MainPage));
    }
}

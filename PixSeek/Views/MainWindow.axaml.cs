using Avalonia.Controls;

namespace PixSeek.Views;
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is PixSeek.ViewModels.MainViewModel vm)
                await vm.InitializeAsync();
        };
    }
}

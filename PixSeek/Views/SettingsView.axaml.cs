using Avalonia.Controls;
using Avalonia.Interactivity;
using PixSeek.Core;
using PixSeek.ViewModels;

namespace PixSeek.Views;
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private void RemoveFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: LibraryFolder f } && DataContext is MainViewModel vm)
            vm.RemoveFolderCommand.Execute(f);
    }

    private async void ToggleRecursive(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { DataContext: LibraryFolder folder } toggle && DataContext is MainViewModel vm)
            await vm.SetRecursiveAsync(folder, toggle.IsChecked == true);
    }
}

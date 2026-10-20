using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using PixSeek.ViewModels;

namespace PixSeek.Views;
public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        AddHandler(ScrollViewer.ScrollChangedEvent, GalleryScrolled);
    }

    private void GalleryScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is ScrollViewer scroll && scroll.Viewport.Height > 0 &&
            scroll.Extent.Height - scroll.Offset.Y - scroll.Viewport.Height < 600 &&
            DataContext is LibraryViewModel vm && vm.HasMore)
            vm.LoadMoreCommand.Execute(null);
    }
    private void GallerySizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
            vm.Reflow(e.NewSize.Width);
    }

    private void OpenMedia(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MediaCardViewModel card } && DataContext is LibraryViewModel vm)
            vm.OpenCommand.Execute(card);
    }

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is LibraryViewModel vm)
        {
            vm.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OpenFromMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MediaCardViewModel card } && DataContext is LibraryViewModel vm)
            vm.OpenCommand.Execute(card);
    }

    private void OpenLocation(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MediaCardViewModel card })
            try
            {
                Services.DesktopActions.OpenFolder(card.Media.FullPath);
            }
            catch (Exception ex)
            {
                (DataContext as LibraryViewModel)?.Error?.Invoke(ex.Message);
            }
    }

    private void OptimizeFile(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MediaCardViewModel card } && DataContext is LibraryViewModel vm)
            vm.CompressMedia(card);
    }
}

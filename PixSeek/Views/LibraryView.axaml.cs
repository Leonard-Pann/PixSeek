using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;
using PixSeek.ViewModels;

namespace PixSeek.Views;
public partial class LibraryView : UserControl
{
    private LibraryViewModel? _subscribed;
    public LibraryView()
    {
        InitializeComponent();
        AddHandler(ScrollViewer.ScrollChangedEvent, GalleryScrolled);
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        Unsubscribe();
        _subscribed = DataContext as LibraryViewModel;
        if (_subscribed != null) _subscribed.ScrollToTopRequested += ScrollToTop;
    }
    private void Unsubscribe()
    {
        if (_subscribed != null) _subscribed.ScrollToTopRequested -= ScrollToTop;
        _subscribed = null;
    }
    private void ScrollToTop() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var scroll in GalleryList.GetVisualDescendants().OfType<ScrollViewer>()) scroll.Offset = default;
    }, DispatcherPriority.Loaded);

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

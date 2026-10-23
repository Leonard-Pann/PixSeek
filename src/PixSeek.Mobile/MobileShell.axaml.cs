using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using PixSeek.Core;

namespace PixSeek.Mobile;
public partial class MobileShell : UserControl
{
    private MobileCell? _held;
    private ScrollViewer? _scroll;
    private MobileViewModel? Model => DataContext as MobileViewModel;
    public MobileShell()
    {
        AvaloniaXamlLoader.Load(this);
        Focusable = true;
        SizeChanged += (_, _) => Model?.UpdateWidth(Bounds.Width);
        Loaded += (_, _) =>
        {
            if (Model is not { } model) return;
            model.UpdateWidth(Bounds.Width);
            var gallery = this.FindControl<ListBox>("Gallery")!;
            _scroll = gallery.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll != null) _scroll.ScrollChanged += OnScrollChanged;
            model.ScrollToTop += () => { if (_scroll != null) _scroll.Offset = new(0, 0); };
        };
    }
    private async void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll != null && _scroll.Offset.Y + _scroll.Viewport.Height > _scroll.Extent.Height - 600 && Model != null)
            await Model.LoadMoreAsync();
    }
    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Model?.Platform.HideKeyboard(); Focus(); e.Handled = true; }
    }
    private void CellTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: MobileCell cell })
        {
            if (ReferenceEquals(cell, _held)) { _held = null; return; }
            cell.Open(cell); e.Handled = true;
        }
    }
    private void CellHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && sender is Control { DataContext: MobileCell cell })
        {
            _held = cell; cell.Select(cell); e.Handled = true;
        }
    }
    private async void AlbumClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DeviceAlbum album } && Model != null) await Model.AddAlbumCommand.ExecuteAsync(album);
    }
    private async void RemoveFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: LibraryFolder folder } && Model != null) await Model.RemoveFolderCommand.ExecuteAsync(folder);
    }
    private async void RemovePersonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: MobileOption person } && Model != null) await Model.RemovePersonCommand.ExecuteAsync(person);
    }
    private async void RecursiveChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: LibraryFolder folder } box && Model != null && box.IsChecked != folder.Recursive)
        {
            try { await Model.Database.SetFolderRecursiveAsync(folder.Id, box.IsChecked == true); Model.Library.QueueScan(); }
            catch (Exception error) { Model.Error = error.Message; }
        }
    }
}

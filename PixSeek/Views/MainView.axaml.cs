using Avalonia;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PixSeek.ViewModels;

namespace PixSeek.Views;
public partial class MainView : UserControl
{
    private MainViewModel? _subscribed;
    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            var selectAll = e.Key == Key.A && (e.KeyModifiers == KeyModifiers.Control ||
                (OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Meta));
            if (!selectAll || DataContext is not MainViewModel { IsGallery: true, IsDialogOpen: false } model ||
                model.Viewer.IsOpen) return;
            // Search text and editable fields retain their native Ctrl+A / Cmd+A behavior.
            if (e.Source is Avalonia.Visual source &&
                (source is TextBox || source.GetVisualAncestors().OfType<TextBox>().Any())) return;
            if (model.Library.ToggleAllCommand.CanExecute(null)) model.Library.ToggleAllCommand.Execute(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DataContext is MainViewModel { IsPersonDialogOpen: true } personVm)
            {
                personVm.People.CancelCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape && DataContext is MainViewModel { IsCompressionDialogOpen: true } vm)
            {
                vm.Optimizer.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    private void Subscribe()
    {
        Unsubscribe();
        if (DataContext is MainViewModel vm)
        {
            _subscribed = vm;
            vm.PropertyChanged += OnViewModelChanged;
            vm.Viewer.PropertyChanged += OnViewerChanged;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed != null)
        {
            _subscribed.PropertyChanged -= OnViewModelChanged;
            _subscribed.Viewer.PropertyChanged -= OnViewerChanged;
        }

        _subscribed = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.IsPersonDialogOpen) && _subscribed?.IsPersonDialogOpen == true)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<PersonDialogView>().FirstOrDefault()?.FindControl<TextBox>("PersonKeywordsInput")?.Focus());
        if (args.PropertyName == nameof(MainViewModel.IsCompressionDialogOpen) && _subscribed?.IsCompressionDialogOpen == true)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<CompressionDialogView>().FirstOrDefault()?.FocusInput());
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MediaViewerViewModel.IsOpen) && _subscribed?.Viewer.IsOpen == true)
            Dispatcher.UIThread.Post(() => this.FindControl<MediaViewerView>("ViewerOverlay")?.Focus());
    }
}

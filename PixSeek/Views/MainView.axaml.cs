using Avalonia;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
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
        KeyDown += (_, e) =>
        {
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
        if (args.PropertyName == nameof(MainViewModel.IsCompressionDialogOpen) && _subscribed?.IsCompressionDialogOpen == true)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<CompressionDialogView>().FirstOrDefault()?.FocusInput());
    }

    private void OnViewerChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MediaViewerViewModel.IsOpen) && _subscribed?.Viewer.IsOpen == true)
            Dispatcher.UIThread.Post(() => this.FindControl<MediaViewerView>("ViewerOverlay")?.Focus());
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using PixSeek.ViewModels;

namespace PixSeek.Views;
public partial class MediaViewerView : UserControl
{
    private MediaViewerViewModel? _vm;
    public MediaViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not MediaViewerViewModel vm)
                return;
            if (e.Key == Key.Escape)
                vm.CloseCommand.Execute(null);
            else if (e.Key == Key.Right)
                vm.NextCommand.Execute(null);
            else if (e.Key == Key.Left)
                vm.PreviousCommand.Execute(null);
            else if (e.Key == Key.Space && vm.IsVideo)
                vm.TogglePlaybackCommand.Execute(null);
            else
                return;
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void Subscribe()
    {
        Unsubscribe();
        if (DataContext is MediaViewerViewModel vm)
        {
            _vm = vm;
            vm.FrameChanged += RenderVideo;
        }
    }

    private void Unsubscribe()
    {
        if (_vm != null)
            _vm.FrameChanged -= RenderVideo;
        _vm = null;
    }

    private void RenderVideo() => this.FindControl<Image>("VideoSurface")?.InvalidateVisual();
}

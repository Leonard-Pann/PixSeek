using Avalonia.Controls;

namespace PixSeek.Views;
public partial class MainWindow : Window
{
    private bool _closing, _readyToClose;
    public MainWindow()
    {
        InitializeComponent();
        Closing += async (_, args) =>
        {
            if (_readyToClose)
                return;
            args.Cancel = true;
            if (_closing)
                return;
            _closing = true;
            IsEnabled = false;
            if (DataContext is PixSeek.ViewModels.MainViewModel model)
                try
                {
                    await model.ShutdownAsync();
                }
                catch (System.Exception e)
                {
                    model.ShowError(e.Message);
                }

            _readyToClose = true;
            Close();
        };
        Opened += async (_, _) =>
        {
            if (DataContext is PixSeek.ViewModels.MainViewModel vm)
                await vm.InitializeAsync();
        };
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PixSeek.ViewModels;
using PixSeek.Views;
using PixSeek.Services;
using Microsoft.Extensions.DependencyInjection;

namespace PixSeek;
public partial class App : Application
{
    public static ServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = AppServices.Create();
        var model = Services.GetRequiredService<MainViewModel>();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = model
            };
            desktop.Exit += (_, _) => model.Stop();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

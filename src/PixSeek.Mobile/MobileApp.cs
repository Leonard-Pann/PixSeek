using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace PixSeek.Mobile;

public sealed class MobileApp : Application
{
    private static readonly TaskCompletionSource<IMobilePlatform> PlatformReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IMobilePlatform? _platform;
    public static IMobilePlatform Platform
    {
        get => _platform ?? throw new InvalidOperationException("Android Activity is not ready.");
        set { _platform = value; PlatformReady.TrySetResult(value); }
    }
    public static MobileViewModel? Model { get; private set; }
    public override void Initialize() { Styles.Add(new FluentTheme()); Styles.Add(MobileTheme.Create()); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IActivityApplicationLifetime activities) activities.MainViewFactory = CreateRoot;
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = CreateRoot();
        base.OnFrameworkInitializationCompleted();
    }
    private static Control CreateRoot()
    {
            var status = new TextBlock { Text = "PixSeek", FontSize = 24, Foreground = Avalonia.Media.Brushes.White, TextAlignment = Avalonia.Media.TextAlignment.Center };
            var loading = new StackPanel { Spacing = 22, Margin = new(28), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            loading.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://PixSeek.Mobile/Assets/PixSeek.png"))), Width = 96, Height = 96 });
            loading.Children.Add(status);
            loading.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 160, Height = 4 });
            var root = new ContentControl { Content = new Border { Background = Avalonia.Media.Brush.Parse("#181924"), Child = loading } };
            _ = StartAsync(root, status);
            return root;
    }
    private static async Task StartAsync(ContentControl root, TextBlock status)
    {
        try
        {
            var platform = await PlatformReady.Task;
            if (platform is IMobileBootstrap bootstrap)
                await bootstrap.PrepareAsync(new Progress<string>(s => status.Text = s), default);
            Model?.Dispose();
            Model = new(platform);
            var view = new MobileShell { DataContext = Model };
            root.Content = view;
            await Model.InitializeAsync();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("PixSeek startup: " + e);
            status.FontSize = 16; status.TextWrapping = Avalonia.Media.TextWrapping.Wrap; status.Margin = new(24);
            status.Text = "PixSeek\n" + e.Message;
        }
    }
}
public interface IMobileBootstrap { Task PrepareAsync(IProgress<string> progress, CancellationToken ct); }

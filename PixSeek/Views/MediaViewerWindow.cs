using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PixSeek.Core;
using PixSeek.Media;
using PixSeek.Services;
using PixSeek.ViewModels;

namespace PixSeek.Views;
public sealed class MediaViewerWindow : Window
{
    private readonly IReadOnlyList<MediaCardViewModel> _cards;
    private int _index;
    private readonly NativeTools _tools;
    private readonly OptimizerViewModel _optimizer;
    private readonly Action _navigate;
    private readonly Image _image = new()
    {
        Stretch = Stretch.Uniform
    };
    private readonly TextBlock _info = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.White
    };
    private readonly ScrollViewer _scroll = new()
    {
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };
    private Bitmap? _bitmap;
    private double _zoom = 1;
    private Point? _drag;
    private Vector _offset;
    private CancellationTokenSource? _loading;
    public MediaViewerWindow(IReadOnlyList<MediaCardViewModel> cards, MediaCardViewModel current, NativeTools tools, OptimizerViewModel optimizer, Action navigate)
    {
        _cards = cards;
        _index = cards.ToList().IndexOf(current);
        _tools = tools;
        _optimizer = optimizer;
        _navigate = navigate;
        Width = 1100;
        Height = 820;
        MinWidth = 800;
        MinHeight = 600;
        Background = Brush.Parse("#14151D");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "PixSeek · Visionneuse";
        var layout = new Grid
        {
            RowDefinitions = new("Auto,*,Auto"),
            Margin = new Thickness(20)
        };
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        void Button(string text, Action action)
        {
            var b = new Button
            {
                Content = text
            };
            b.Click += (_, _) => action();
            bar.Children.Add(b);
        }

        Button("← Précédent", () => Move(-1));
        Button("Suivant →", () => Move(1));
        Button("−", () => Zoom(.8));
        Button("＋", () => Zoom(1.25));
        Button("Ajuster", () =>
        {
            _zoom = 1;
            UpdateZoom();
        });
        Button("Plein écran", () => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen);
        Button("Fermer", Close);
        layout.Children.Add(bar);
        _scroll.Content = _image;
        Grid.SetRow(_scroll, 1);
        layout.Children.Add(_scroll);
        var bottom = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(0, 14, 0, 0)
        };
        bottom.Children.Add(_info);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12
        };
        var location = new Button
        {
            Content = "Ouvrir l'emplacement"
        };
        location.Click += (_, _) => DesktopActions.OpenFolder(_cards[_index].Media.FullPath);
        actions.Children.Add(location);
        var optimize = new Button
        {
            Content = "Compresser ce fichier"
        };
        optimize.Click += (_, _) =>
        {
            _optimizer.Prepare([_cards[_index].Media.FullPath]);
            _navigate();
            Close();
        };
        actions.Children.Add(optimize);
        bottom.Children.Add(actions);
        Grid.SetRow(bottom, 2);
        layout.Children.Add(bottom);
        Content = layout;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key == Key.Left)
                Move(-1);
            else if (e.Key == Key.Right)
                Move(1);
            else if (e.Key is Key.Add or Key.OemPlus)
                Zoom(1.25);
            else if (e.Key is Key.Subtract or Key.OemMinus)
                Zoom(.8);
        };
        _scroll.PointerWheelChanged += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                Zoom(e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
                e.Handled = true;
            }
        };
        _image.PointerPressed += (_, e) =>
        {
            _drag = e.GetPosition(_scroll);
            _offset = _scroll.Offset;
            e.Pointer.Capture(_image);
        };
        _image.PointerMoved += (_, e) =>
        {
            if (_drag is { } start)
            {
                var p = e.GetPosition(_scroll);
                _scroll.Offset = _offset + new Vector(start.X - p.X, start.Y - p.Y);
            }
        };
        _image.PointerReleased += (_, e) =>
        {
            _drag = null;
            e.Pointer.Capture(null);
        };
        Opened += async (_, _) => await LoadAsync();
        Closed += (_, _) =>
        {
            _loading?.Cancel();
            _bitmap?.Dispose();
        };
    }

    private void Move(int delta)
    {
        _index = (_index + delta + _cards.Count) % _cards.Count;
        _zoom = 1;
        _ = LoadAsync();
    }

    private void Zoom(double factor)
    {
        _zoom = Math.Clamp(_zoom * factor, .1, 8);
        UpdateZoom();
    }

    private void UpdateZoom()
    {
        if (_bitmap == null)
            return;
        _image.Width = Math.Min(_bitmap.PixelSize.Width, Math.Max(500, Bounds.Width - 80)) * _zoom;
        _image.Height = double.NaN;
    }

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        _loading = new();
        var ct = _loading.Token;
        var card = _cards[_index];
        var m = card.Media;
        Title = "PixSeek · " + m.FileName;
        _info.Text = $"{m.FileName} · {m.Width} × {m.Height} · {MediaFormats.Size(m.SizeBytes)} · {m.LastModifiedAt.ToLocalTime():g}\n{m.FullPath}\n{card.Score}";
        if (card.IsVideo)
        {
            try
            {
                DesktopActions.PlayVideo(m, card.Timestamp, _tools);
            }
            catch (Exception e)
            {
                _info.Text = e.Message;
            }

            return;
        }

        var preview = Path.Combine(Path.GetTempPath(), "pixseek-preview-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            await App.Services.GetRequiredImageService().ThumbnailAsync(m.FullPath, preview, 3200, ct);
            var bitmap = await Task.Run(() =>
            {
                using var s = File.OpenRead(preview);
                return new Bitmap(s);
            }, ct);
            if (ct.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            _image.Source = null;
            _bitmap?.Dispose();
            _bitmap = bitmap;
            _image.Source = bitmap;
            UpdateZoom();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _info.Text += "\n" + e.Message;
        }
        finally
        {
            if (File.Exists(preview))
                File.Delete(preview);
        }
    }
}

internal static class ViewerServices
{
    public static ImageMagickService GetRequiredImageService(this Microsoft.Extensions.DependencyInjection.ServiceProvider services) => Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ImageMagickService>(services);
}

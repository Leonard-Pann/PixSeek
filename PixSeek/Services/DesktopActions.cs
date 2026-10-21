using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using PixSeek.Core;

namespace PixSeek.Services;
public static class DesktopActions
{
    public static async Task<IReadOnlyList<string>> PickImagesAsync(string title)
    {
        if (MainWindow == null) return [];
        var files = await MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Photos") { Patterns = MediaFormats.Images.Select(e => "*" + e).ToArray() }]
        });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToArray();
    }
    public static Window? MainWindow => (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static async Task<string?> PickFolderAsync(string title)
    {
        if (MainWindow == null)
            return null;
        var result = await MainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public static void OpenFolder(string path)
    {
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using PixSeek.Localization;

namespace PixSeek.Services;

public static class MediaClipboard
{
    public static async Task CopyAsync(TopLevel owner, string path, Bitmap? image, bool video)
    {
        var clipboard = owner.Clipboard ?? throw new InvalidOperationException(TranslationService.Text("ClipboardUnavailable"));
        var file = await owner.StorageProvider.TryGetFileFromPathAsync(path)
            ?? throw new FileNotFoundException(path);
        var item = new DataTransferItem();
        item.SetFile(file);
        Bitmap? snapshot = null;
        if (!video && image != null)
        {
            // Detach clipboard lifetime from the viewer cache; an application can paste after navigation/close.
            using var bytes = new MemoryStream();
            image.Save(bytes, PngBitmapEncoderOptions.Default);
            bytes.Position = 0;
            snapshot = new Bitmap(bytes);
            item.SetBitmap(snapshot);
        }
        var data = new DataTransfer();
        data.Add(item);
        var payload = new OwnedTransfer(data, snapshot);
        try { await clipboard.SetDataAsync(payload); }
        catch { payload.Dispose(); throw; }
        await clipboard.FlushAsync();
    }

    private sealed class OwnedTransfer(DataTransfer data, Bitmap? bitmap) : IAsyncDataTransfer, IDataTransfer
    {
        public IReadOnlyList<DataFormat> Formats => data.Formats;
        public IReadOnlyList<IDataTransferItem> Items => data.Items;
        IReadOnlyList<IAsyncDataTransferItem> IAsyncDataTransfer.Items => data.Items.Select(i => (IAsyncDataTransferItem)new AsyncItem(i)).ToArray();
        public void Dispose() { bitmap?.Dispose(); }
    }
    private sealed class AsyncItem(IDataTransferItem item) : IAsyncDataTransferItem
    {
        public IReadOnlyList<DataFormat> Formats => item.Formats;
        public Task<object?> TryGetRawAsync(DataFormat format) => Task.FromResult(item.TryGetRaw(format));
    }
}

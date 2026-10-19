using Avalonia.Controls;
using Avalonia.Input;

namespace PixSeek.Controls;
/// <summary>Virtualized rows are layout containers; only individual media checkboxes select files.</summary>
public sealed class MediaGallery : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new GalleryRow();
    protected override void OnKeyDown(KeyEventArgs e)
    {
    }

    private sealed class GalleryRow : ListBoxItem
    {
        protected override Type StyleKeyOverride => typeof(ListBoxItem);

        public GalleryRow() => Focusable = false;
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
        }
    }
}

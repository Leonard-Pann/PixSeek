using Avalonia.Controls;
using Avalonia.Input;

namespace PixSeek.Mobile;
public sealed class MobileTile : Border
{
    public MobileTile() => SetValue(InputElement.IsHoldingEnabledProperty, true);
}

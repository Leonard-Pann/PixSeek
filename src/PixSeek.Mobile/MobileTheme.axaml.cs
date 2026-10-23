using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace PixSeek.Mobile;
public partial class MobileTheme : Styles
{
    public MobileTheme() => AvaloniaXamlLoader.Load(this);
    public static MobileTheme Create() => new();
}

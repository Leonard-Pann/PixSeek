using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using PixSeek.Mobile;

namespace PixSeek.Android;
[Application]
public sealed class MainApplication(nint handle, JniHandleOwnership ownership) : AvaloniaAndroidApplication<MobileApp>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder).WithInterFont();
}

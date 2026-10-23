using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Fonts.Inter;
using PixSeek.Mobile;

namespace PixSeek.Android;

[Activity(Label = "PixSeek", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.KeyboardHidden | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    LaunchMode = LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class MainActivity : AvaloniaMainActivity
{
    public AndroidPlatform Platform { get; private set; } = null!;
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Platform = new(this);
        MobileApp.Platform = Platform;
        base.OnCreate(savedInstanceState);
        BackRequested += (_, e) => { if (MobileApp.Model?.Back() == true) e.Handled = true; };
    }
    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (Platform.OnResult(requestCode, resultCode, data)) return;
        base.OnActivityResult(requestCode, resultCode, data);
    }
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        Platform.OnPermissions(requestCode);
    }
    protected override void OnPause() { MobileApp.Model?.Suspend(); base.OnPause(); }
    protected override void OnResume() { base.OnResume(); if (MobileApp.Model != null) _ = MobileApp.Model.ResumeAsync(); }
}

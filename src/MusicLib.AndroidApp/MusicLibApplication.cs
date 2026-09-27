using Android.App;
using Android.Runtime;

namespace MusicLib.AndroidApp;

// LibVLC needs Core.Initialize(context) once, before the first LibVLC() is constructed.
[Application]
public class MusicLibApplication : Application
{
    public MusicLibApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate()
    {
        base.OnCreate();
        LibVLCSharp.Shared.Core.Initialize();
    }
}

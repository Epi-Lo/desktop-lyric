using Windows.Storage.Streams;
using WinRT;

namespace DesktopLyric;

/// <summary>
/// CsWinRT SMTC wrappers finalize on a non-STA thread and AV
/// (WinRT.IObjectReference.Finalize → c0000005). Closing a session /
/// PlaybackInfo / Timeline NativeObject on the UI thread also drops the
/// live SMTC COM ref, so the clock then reads 0 or throws and lyrics
/// stop following.
///
/// IRandomAccessStream is IClosable: Dispose it only if this app still owns
/// it. <see cref="System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead"/>
/// takes ownership and will Close the stream — a second Dispose heap-corrupts
/// (ntdll 0xc0000374). In that case call <see cref="Suppress"/> only.
/// Other wrappers: suppress the finalizer and leave the ref.
/// </summary>
internal static class WinRtLifetime
{
    /// <summary>
    /// Close an owned IRandomAccessStream. Everything else: suppress the
    /// CsWinRT finalizer so GC does not Release COM on an MTA thread.
    /// </summary>
    public static void Release(object? obj)
    {
        if (obj == null) return;
        try
        {
            if (obj is IRandomAccessStream stream)
            {
                stream.Dispose();
                return;
            }
            Suppress(obj);
        }
        catch { }
    }

    /// <summary>
    /// Stop the CsWinRT finalizer without Close/Dispose. Use after another
    /// owner already closed the object, or for SMTC snapshots we must not
    /// NativeObject.Dispose.
    /// </summary>
    public static void Suppress(object? obj)
    {
        if (obj == null) return;
        try { GC.SuppressFinalize(obj); }
        catch { }
        try
        {
            if (obj is IWinRTObject w)
            {
                try { GC.SuppressFinalize(w); } catch { }
                try
                {
                    var native = w.NativeObject;
                    if (native != null)
                        GC.SuppressFinalize(native);
                }
                catch { }
            }
        }
        catch { }
    }
}

using Xunit;

namespace DesktopLyric.Tests;

public class WinRtLifetimeTests
{
    [Fact]
    public void release_and_suppress_null_do_not_throw()
    {
        WinRtLifetime.Release(null);
        WinRtLifetime.Suppress(null);
    }

    [Fact]
    public void release_managed_objects_do_not_throw()
    {
        WinRtLifetime.Release("smtc");
        WinRtLifetime.Suppress(new object());
        using var ms = new MemoryStream();
        WinRtLifetime.Release(ms);
        WinRtLifetime.Suppress(ms);
    }

    [Fact]
    public void suppress_after_dispose_does_not_throw()
    {
        var ms = new MemoryStream();
        ms.Dispose();
        WinRtLifetime.Suppress(ms);
        WinRtLifetime.Release(ms);
    }
}

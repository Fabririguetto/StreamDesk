using StreamDesk.Common.Power;
using Xunit;

namespace StreamDesk.Tests;

public class ScreenKeepAwakeTests
{
    [Fact]
    public void TestScreenKeepAwakeLifecycle()
    {
        using (var keepAwake = new ScreenKeepAwake(autoStart: true, heartbeatIntervalSeconds: 1))
        {
            Assert.True(keepAwake.IsActive);

            keepAwake.Disable();
            Assert.False(keepAwake.IsActive);

            keepAwake.Enable();
            Assert.True(keepAwake.IsActive);
        }

        // Al salir del using ya debe estar deshabilitado
    }
}

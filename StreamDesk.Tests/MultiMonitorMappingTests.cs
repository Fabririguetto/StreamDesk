using StreamDesk.Common.Input;
using Xunit;

namespace StreamDesk.Tests;

public class MultiMonitorMappingTests
{
    [Fact]
    public void MapToVirtualDesktop_WhenTargetDisplayIs255_ReturnsOriginalNormalizedCoordinates()
    {
        // 255 significa todo el escritorio virtual sin transformación por monitor individual
        using var simulator = new Win32InputSimulator(5099);
        
        var (x, y) = simulator.MapToVirtualDesktop(32768, 16384, 255);

        Assert.Equal(32768, x);
        Assert.Equal(16384, y);
    }

    [Fact]
    public void MapToVirtualDesktop_CoordinatesStayWithin0To65535()
    {
        using var simulator = new Win32InputSimulator(5098);

        // Probar bordes (0, 0) y (65535, 65535)
        var (minX, minY) = simulator.MapToVirtualDesktop(0, 0, 0);
        Assert.InRange(minX, 0, 65535);
        Assert.InRange(minY, 0, 65535);

        var (maxX, maxY) = simulator.MapToVirtualDesktop(65535, 65535, 0);
        Assert.InRange(maxX, 0, 65535);
        Assert.InRange(maxY, 0, 65535);
    }

    [Fact]
    public void Displays_List_IsNotNull()
    {
        using var simulator = new Win32InputSimulator(5097);
        Assert.NotNull(simulator.Displays);
    }
}

using StreamDesk.Common.Input;
using StreamDesk.Common.Network;
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
    public void MapToVirtualDesktop_AutoMode_EdgeTransitionCoordinatesStayValid()
    {
        using var simulator = new Win32InputSimulator(5096);

        // En modo auto (0):
        var (x1, y1) = simulator.MapToVirtualDesktop(10000, 20000, 0);
        Assert.InRange(x1, 0, 65535);
        Assert.InRange(y1, 0, 65535);

        // Si empuja al borde derecho (65500), conmuta
        var (x2, y2) = simulator.MapToVirtualDesktop(65500, 20000, 0);
        Assert.InRange(x2, 0, 65535);
        Assert.InRange(y2, 0, 65535);

        // Si empuja al borde izquierdo (50), conmuta de vuelta
        var (x3, y3) = simulator.MapToVirtualDesktop(50, 20000, 0);
        Assert.InRange(x3, 0, 65535);
        Assert.InRange(y3, 0, 65535);
    }

    [Fact]
    public void Displays_List_IsNotNull()
    {
        using var simulator = new Win32InputSimulator(5097);
        Assert.NotNull(simulator.Displays);
    }
}

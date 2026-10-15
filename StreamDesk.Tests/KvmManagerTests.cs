using StreamDesk.Monitor.Input;
using Xunit;

namespace StreamDesk.Tests;

public class KvmManagerTests
{
    [Fact]
    public void KvmManager_DefaultState_IsControlEnabled()
    {
        using var kvm = new LowLevelKvmManager("127.0.0.1", 5005);
        Assert.True(kvm.IsControlEnabled);
        Assert.True(kvm.IsKvmActive);
    }

    [Fact]
    public void KvmManager_ToggleControl_TogglesStateCorrectly()
    {
        using var kvm = new LowLevelKvmManager("127.0.0.1", 5005);
        
        bool newState = kvm.ToggleControl();
        Assert.False(newState);
        Assert.False(kvm.IsControlEnabled);
        Assert.False(kvm.IsKvmActive);

        newState = kvm.ToggleControl();
        Assert.True(newState);
        Assert.True(kvm.IsControlEnabled);
        Assert.True(kvm.IsKvmActive);
    }

    [Fact]
    public void KvmManager_StateChangedEvent_FiresOnToggle()
    {
        using var kvm = new LowLevelKvmManager("127.0.0.1", 5005);
        bool eventFired = false;
        bool lastState = true;

        kvm.OnKvmStateChanged += state =>
        {
            eventFired = true;
            lastState = state;
        };

        kvm.IsControlEnabled = false;
        Assert.True(eventFired);
        Assert.False(lastState);

        eventFired = false;
        kvm.ToggleControl();
        Assert.True(eventFired);
        Assert.True(lastState);
    }
}

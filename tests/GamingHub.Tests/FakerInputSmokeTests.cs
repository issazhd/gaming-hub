using GamingHub.Infrastructure.Input;
using Xunit;

namespace GamingHub.Tests;

public class FakerInputSmokeTests
{
    /// <summary>The HID search runs on any PC (driver installed or not) without throwing; without the driver nothing is sent.</summary>
    [Fact]
    public void Search_and_send_are_safe_without_the_driver()
    {
        bool installed = FakerInputMouse.IsInstalled();
        using var m = new FakerInputMouse();
        if (!installed) Assert.False(m.Move(0, 0));
    }
}

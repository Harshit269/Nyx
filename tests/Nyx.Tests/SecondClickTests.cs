using PhoneBridge;
using Xunit;

namespace Nyx.Tests;

public class SecondClickTests
{
    [Fact]
    public void Only_a_click_close_to_the_first_counts_as_the_double_click()
    {
        Assert.True(SecondClick.Near(1500, 1000, 1500, 1000, 4, 4));
        Assert.True(SecondClick.Near(1500, 1000, 1507, 993, 4, 4));
        Assert.False(SecondClick.Near(1500, 1000, 1520, 1000, 4, 4));
        Assert.False(SecondClick.Near(1500, 1000, 1500, 950, 4, 4));
    }
}

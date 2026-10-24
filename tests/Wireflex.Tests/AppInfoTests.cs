using Wireflex.Core;

namespace Wireflex.Tests;

public class AppInfoTests
{
    [Fact]
    public void Name_IsWireflex()
    {
        Assert.Equal("Wireflex", AppInfo.Name);
    }
}

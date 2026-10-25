using Wireflex.Core.Logging;

namespace Wireflex.Tests.Logging;

public class SensitiveTests
{
    [Theory]
    [InlineData("TESTSERIAL01", "***AL01")]
    [InlineData("ABCD", "***")]
    [InlineData("", "(vazio)")]
    [InlineData(null, "(vazio)")]
    public void Mask_ShowsOnlyLastFourCharacters(string? value, string expected)
    {
        Assert.Equal(expected, Sensitive.Mask(value));
    }

    [Theory]
    [InlineData("TESTSERIAL01", "***AL01")]
    [InlineData("192.0.2.10:5555", "***2.10:5555")]
    [InlineData("adb-TESTSERIAL02-AbCdEf._adb-tls-connect._tcp", "mdns:***AL02")]
    [InlineData("emulator-5554", "emulator-5554")]
    public void MaskAdbSerial_ByFormat(string adbSerial, string expected)
    {
        Assert.Equal(expected, Sensitive.MaskAdbSerial(adbSerial));
    }

    [Theory]
    [InlineData("conectado em 192.0.2.10:5555", "conectado em ***2.10:5555")]
    [InlineData("falhou em 192.0.2.10.", "falhou em ***2.10.")]
    [InlineData("ip 192.0.2.25. fim", "ip ***2.25. fim")]
    public void ScrubIpAddresses_MasksIpv4IncludingEndOfSentence(string text, string expected)
    {
        Assert.Equal(expected, Sensitive.ScrubIpAddresses(text));
    }

    [Theory]
    [InlineData("[2001:db8::1]:5555 erro", "2001:db8")]
    [InlineData("endereço 2001:db8:0:0:0:0:0:1 visto", "2001:db8")]
    [InlineData("host 2001:db8::a1b2:c3d4", "2001:db8")]
    public void ScrubIpAddresses_MasksIpv6(string text, string mustNotContain)
    {
        var scrubbed = Sensitive.ScrubIpAddresses(text);

        Assert.DoesNotContain(mustNotContain, scrubbed, StringComparison.Ordinal);
        Assert.Contains("***", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-10-24 12:40:16.696 [INF] ok")]
    [InlineData("adb 37.0.0-14910828")]
    [InlineData("1.2.3.4.5 não é IP")]
    public void ScrubIpAddresses_LeavesNonAddressesAlone(string text)
    {
        Assert.Equal(text, Sensitive.ScrubIpAddresses(text));
    }

    [Theory]
    [InlineData(@"C:\Users\TestUser\AppData\Roaming\Wireflex", @"C:\Users\%USERNAME%\AppData\Roaming\Wireflex")]
    [InlineData(@"D:\testuser\Documents\adb.exe", @"D:\%USERNAME%\Documents\adb.exe")]
    [InlineData(@"pasta C:\Users\TestUser fim", @"pasta C:\Users\%USERNAME% fim")]
    [InlineData(@"C:\Users\TestUserX\AppData", @"C:\Users\TestUserX\AppData")]
    [InlineData("TestUser sem caminho", "TestUser sem caminho")]
    public void ScrubUserName_ReplacesUserFolderInPaths(string text, string expected)
    {
        Assert.Equal(expected, Sensitive.ScrubUserName(text, "TestUser"));
    }

    [Fact]
    public void ScrubUserName_EmptyUserName_LeavesTextAlone()
    {
        Assert.Equal(@"C:\Users\TestUser", Sensitive.ScrubUserName(@"C:\Users\TestUser", " "));
    }

    [Fact]
    public void ScrubKnown_MasksKnownSerialsInThirdPartyOutput()
    {
        var line = "INFO: ADB device found:\n    -->   (usb)  TESTSERIAL01            device  Test_Phone\n    (tcpip) 192.0.2.10:5555";

        var scrubbed = Sensitive.ScrubKnown(line, "TESTSERIAL01", "192.0.2.10:5555", null);

        Assert.DoesNotContain("TESTSERIAL01", scrubbed);
        Assert.DoesNotContain("192.0.2.10", scrubbed);
        Assert.Contains("***AL01", scrubbed);
    }

    [Fact]
    public void Scrub_UsesCurrentWindowsUser()
    {
        var path = Path.Combine(@"C:\Users", Environment.UserName, "AppData");

        var scrubbed = Sensitive.Scrub(path + " em 192.0.2.10");

        Assert.Equal(@"C:\Users\%USERNAME%\AppData em ***2.10", scrubbed);
    }
}

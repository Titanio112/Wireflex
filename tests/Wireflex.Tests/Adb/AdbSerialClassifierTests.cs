using Wireflex.Core.Adb;

namespace Wireflex.Tests.Adb;

public class AdbSerialClassifierTests
{
    [Theory]
    [InlineData("TESTSERIAL01", AdbTransport.Usb)]
    [InlineData("192.0.2.10:5555", AdbTransport.WifiTcpip)]
    [InlineData("[2001:db8::1]:5555", AdbTransport.WifiTcpip)]
    [InlineData("testphone.local:5555", AdbTransport.WifiTcpip)]
    [InlineData("adb-TESTSERIAL02-AbCdEf._adb-tls-connect._tcp", AdbTransport.WifiMdns)]
    [InlineData("adb-TESTSERIAL02-AbCdEf._adb-tls-connect._tcp.", AdbTransport.WifiMdns)]
    [InlineData("adb-TESTSERIAL02-AbCdEf._adb._tcp", AdbTransport.WifiMdns)]
    [InlineData("emulator-5554", AdbTransport.Emulator)]
    [InlineData("192.0.2.10:0", AdbTransport.Usb)]
    [InlineData("192.0.2.10:70000", AdbTransport.Usb)]
    [InlineData("2001:db8::1:5555", AdbTransport.Usb)]
    public void Classify(string serial, AdbTransport expected)
    {
        Assert.Equal(expected, AdbSerialClassifier.Classify(serial));
    }

    [Theory]
    [InlineData("192.0.2.10:5555", "192.0.2.10", 5555)]
    [InlineData("[2001:db8::1]:37000", "2001:db8::1", 37000)]
    public void TryParseHostPort_Valid(string serial, string host, int port)
    {
        Assert.True(AdbSerialClassifier.TryParseHostPort(serial, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("adb-TESTSERIAL02-AbCdEf._adb-tls-connect._tcp", "TESTSERIAL02")]
    [InlineData("adb-TESTSERIAL02-AbCdEf._adb-tls-connect._tcp.", "TESTSERIAL02")]
    [InlineData("adb-TEST-SERIAL-02-AbCdEf._adb-tls-connect._tcp", "TEST-SERIAL-02")]
    [InlineData("adb-TESTSERIAL02._adb-tls-connect._tcp", "TESTSERIAL02")]
    public void TryExtractMdnsSerial_Valid(string serial, string expected)
    {
        Assert.True(AdbSerialClassifier.TryExtractMdnsSerial(serial, out var phoneSerial));
        Assert.Equal(expected, phoneSerial);
    }

    [Theory]
    [InlineData("TESTSERIAL01")]
    [InlineData("192.0.2.10:5555")]
    [InlineData("outro-TESTSERIAL02._adb-tls-connect._tcp")]
    public void TryExtractMdnsSerial_NotMdns(string serial)
    {
        Assert.False(AdbSerialClassifier.TryExtractMdnsSerial(serial, out _));
    }
}

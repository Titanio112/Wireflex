using Wireflex.Core.Adb;

namespace Wireflex.Tests.Adb;

// Seriais e IPs fictícios: TESTSERIALnn e a faixa de documentação 192.0.2.0/24 e 2001:db8::/32.
public class AdbDeviceListParserTests
{
    [Fact]
    public void Parse_DevicesLongOutput_ReadsUsbTcpipAndMdnsLines()
    {
        const string output =
            "List of devices attached\n" +
            "TESTSERIAL01           device usb:1-1 product:testprod model:Test_Phone device:testdev transport_id:3\n" +
            "192.0.2.10:5555        device product:testprod model:Test_Phone device:testdev transport_id:4\n" +
            "adb-TESTSERIAL02-AbCdEf._adb-tls-connect._tcp device product:otherprod model:Other device:otherdev transport_id:5\n" +
            "\n";

        var devices = AdbDeviceListParser.Parse(output);

        Assert.Equal(3, devices.Count);

        var usb = devices[0];
        Assert.Equal("TESTSERIAL01", usb.Serial);
        Assert.Equal(AdbDeviceState.Device, usb.State);
        Assert.Equal(AdbTransport.Usb, usb.Transport);
        Assert.Equal("usb:1-1", usb.UsbPath);
        Assert.Equal("testprod", usb.Product);
        Assert.Equal("Test_Phone", usb.Model);
        Assert.Equal("Test Phone", usb.DisplayModel);
        Assert.Equal("testdev", usb.DeviceName);
        Assert.Equal(3, usb.TransportId);

        var tcpip = devices[1];
        Assert.Equal("192.0.2.10:5555", tcpip.Serial);
        Assert.Equal(AdbTransport.WifiTcpip, tcpip.Transport);
        Assert.Null(tcpip.UsbPath);
        Assert.Equal(4, tcpip.TransportId);

        var mdns = devices[2];
        Assert.Equal(AdbTransport.WifiMdns, mdns.Transport);
        Assert.Equal("Other", mdns.Model);
        Assert.Equal(5, mdns.TransportId);
    }

    [Theory]
    [InlineData("device", AdbDeviceState.Device)]
    [InlineData("unauthorized", AdbDeviceState.Unauthorized)]
    [InlineData("offline", AdbDeviceState.Offline)]
    [InlineData("authorizing", AdbDeviceState.Authorizing)]
    [InlineData("connecting", AdbDeviceState.Connecting)]
    [InlineData("bootloader", AdbDeviceState.Bootloader)]
    [InlineData("recovery", AdbDeviceState.Recovery)]
    [InlineData("rescue", AdbDeviceState.Rescue)]
    [InlineData("sideload", AdbDeviceState.Sideload)]
    [InlineData("host", AdbDeviceState.Host)]
    [InlineData("detached", AdbDeviceState.Detached)]
    [InlineData("estado-novo", AdbDeviceState.Unknown)]
    public void Parse_ReadsEveryState(string stateText, AdbDeviceState expected)
    {
        var devices = AdbDeviceListParser.Parse($"TESTSERIAL01           {stateText} usb:1-1 transport_id:1\n");

        var device = Assert.Single(devices);
        Assert.Equal(expected, device.State);
        Assert.Equal(1, device.TransportId);
    }

    [Fact]
    public void Parse_UnauthorizedWithoutModel_HasNoDescriptionFields()
    {
        var device = Assert.Single(AdbDeviceListParser.Parse("TESTSERIAL03           unauthorized usb:1-2 transport_id:6\n"));

        Assert.Equal(AdbDeviceState.Unauthorized, device.State);
        Assert.Null(device.Model);
        Assert.Null(device.Product);
        Assert.Equal("usb:1-2", device.UsbPath);
    }

    [Fact]
    public void Parse_NoPermissionsWithHelpText_KeepsFieldsAfterIt()
    {
        const string line =
            "TESTSERIAL04           no permissions (missing udev rules? user is in the plugdev group); " +
            "see [http://developer.android.com/tools/device.html] usb:1-3 transport_id:7\n";

        var device = Assert.Single(AdbDeviceListParser.Parse(line));

        Assert.Equal("TESTSERIAL04", device.Serial);
        Assert.Equal(AdbDeviceState.NoPermissions, device.State);
        Assert.Equal("usb:1-3", device.UsbPath);
        Assert.Equal(7, device.TransportId);
    }

    [Fact]
    public void Parse_ShortFormatWithTab()
    {
        var devices = AdbDeviceListParser.Parse("TESTSERIAL01\tdevice\r\n192.0.2.11:5555\toffline\r\n");

        Assert.Equal(2, devices.Count);
        Assert.Equal(AdbDeviceState.Device, devices[0].State);
        Assert.Equal(AdbTransport.WifiTcpip, devices[1].Transport);
        Assert.Equal(AdbDeviceState.Offline, devices[1].State);
    }

    [Fact]
    public void Parse_NoSerialNumber()
    {
        var device = Assert.Single(AdbDeviceListParser.Parse("(no serial number)     device usb:1-4 transport_id:8\n"));

        Assert.Equal("(no serial number)", device.Serial);
        Assert.Equal(AdbDeviceState.Device, device.State);
    }

    [Fact]
    public void Parse_Ipv6HostPort_IsTcpip()
    {
        var device = Assert.Single(AdbDeviceListParser.Parse("[2001:db8::1]:5555     device transport_id:9\n"));

        Assert.Equal(AdbTransport.WifiTcpip, device.Transport);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("List of devices attached\n\n")]
    [InlineData("* daemon not running; starting now at tcp:5037\n* daemon started successfully\nList of devices attached\n")]
    public void Parse_EmptyOutput_ReturnsNoDevices(string output)
    {
        Assert.Empty(AdbDeviceListParser.Parse(output));
    }

    [Theory]
    [InlineData("TESTSERIAL05")]
    [InlineData("TESTSERIAL05   ")]
    [InlineData("   ")]
    [InlineData("\tdevice")]
    public void TryParseLine_MalformedLine_ReturnsFalse(string line)
    {
        Assert.False(AdbDeviceListParser.TryParseLine(line, out _));
    }

    [Fact]
    public void Parse_SkipsMalformedLinesAndKeepsValidOnes()
    {
        var devices = AdbDeviceListParser.Parse("TESTSERIAL05\nTESTSERIAL01           device transport_id:1\n");

        Assert.Equal("TESTSERIAL01", Assert.Single(devices).Serial);
    }
}

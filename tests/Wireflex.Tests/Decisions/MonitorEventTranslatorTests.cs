using Wireflex.Core.Adb;
using Wireflex.Core.Decisions;
using Wireflex.Core.Monitoring;

namespace Wireflex.Tests.Decisions;

public class MonitorEventTranslatorTests
{
    [Fact]
    public void WifiWithoutSerial_DoesNotReachEngine()
    {
        var unidentified = new DeviceConnection("192.0.2.10:5555", null, AdbTransport.WifiTcpip, AdbDeviceState.Device, null);

        Assert.Null(MonitorEventTranslator.Translate(new DeviceAppeared(unidentified)));
        Assert.Null(MonitorEventTranslator.Translate(new DeviceStateChanged(unidentified, AdbDeviceState.Offline)));
        Assert.Null(MonitorEventTranslator.Translate(new DeviceDisappeared(unidentified)));
    }

    [Fact]
    public void WifiWithSerial_UsesPhoneSerialAsIdentity()
    {
        var identified = new DeviceConnection("192.0.2.10:5555", "TESTSERIAL01", AdbTransport.WifiTcpip, AdbDeviceState.Device, null);

        var input = Assert.IsType<ConnectionAdded>(MonitorEventTranslator.Translate(new DeviceAppeared(identified)));

        Assert.Equal("TESTSERIAL01", input.Serial);
        Assert.Equal("192.0.2.10:5555", input.AdbSerial);
        Assert.Equal(AdbTransport.WifiTcpip, input.Transport);
    }

    [Fact]
    public void Usb_AlwaysReachesEngine()
    {
        var usb = new DeviceConnection("TESTSERIAL01", "TESTSERIAL01", AdbTransport.Usb, AdbDeviceState.Unauthorized, null);

        Assert.IsType<ConnectionAdded>(MonitorEventTranslator.Translate(new DeviceAppeared(usb)));
        Assert.IsType<ConnectionStateChanged>(MonitorEventTranslator.Translate(new DeviceStateChanged(usb, AdbDeviceState.Offline)));
        Assert.IsType<ConnectionRemoved>(MonitorEventTranslator.Translate(new DeviceDisappeared(usb)));
    }

    [Fact]
    public void Emulator_IsIgnored()
    {
        var emulator = new DeviceConnection("emulator-5554", "emulator-5554", AdbTransport.Emulator, AdbDeviceState.Device, null);

        Assert.Null(MonitorEventTranslator.Translate(new DeviceAppeared(emulator)));
    }

    [Fact]
    public void AdbAvailable_ReevaluatesAll_LostDoesNothing()
    {
        var reevaluate = Assert.IsType<Reevaluate>(MonitorEventTranslator.Translate(new MonitorStatusChanged(true, null)));

        Assert.Null(reevaluate.Serial);
        Assert.Null(MonitorEventTranslator.Translate(new MonitorStatusChanged(false, "caiu")));
        Assert.Null(MonitorEventTranslator.Translate(new DeviceTransportChanged("TESTSERIAL01", AdbTransport.Usb, AdbTransport.WifiTcpip)));
    }
}

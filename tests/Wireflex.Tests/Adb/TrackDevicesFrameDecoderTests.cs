using System.Globalization;
using System.Text;
using Wireflex.Core.Adb;

namespace Wireflex.Tests.Adb;

public class TrackDevicesFrameDecoderTests
{
    private static string Frame(string payload) =>
        Encoding.UTF8.GetByteCount(payload).ToString("x4", CultureInfo.InvariantCulture) + payload;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Append_SingleFrame()
    {
        const string payload = "TESTSERIAL01           device usb:1-1 transport_id:1\n";

        var payloads = new TrackDevicesFrameDecoder().Append(Bytes(Frame(payload)));

        Assert.Equal(payload, Assert.Single(payloads));
    }

    [Fact]
    public void Append_HeaderIsLengthInHex()
    {
        // 0x1a = 26 bytes.
        const string payload = "192.0.2.10:5555\tdevice\nAB\n";
        Assert.Equal(26, payload.Length);

        var payloads = new TrackDevicesFrameDecoder().Append(Bytes("001a" + payload));

        Assert.Equal(payload, Assert.Single(payloads));
    }

    [Fact]
    public void Append_TwoFramesInOneChunk()
    {
        var first = "TESTSERIAL01\tdevice\n";
        var second = "TESTSERIAL01\tdevice\n192.0.2.10:5555\tdevice\n";

        var payloads = new TrackDevicesFrameDecoder().Append(Bytes(Frame(first) + Frame(second)));

        Assert.Equal(new[] { first, second }, payloads);
    }

    [Fact]
    public void Append_FrameSplitAcrossChunks()
    {
        const string payload = "adb-TESTSERIAL02-AbCdEf._adb-tls-connect._tcp device transport_id:5\n";
        var bytes = Bytes(Frame(payload));
        var decoder = new TrackDevicesFrameDecoder();
        var results = new List<string>();

        foreach (var b in bytes)
        {
            results.AddRange(decoder.Append([b]));
        }

        Assert.Equal(payload, Assert.Single(results));
    }

    [Fact]
    public void Append_EmptyList_IsZeroLengthFrame()
    {
        var payloads = new TrackDevicesFrameDecoder().Append(Bytes("0000"));

        Assert.Equal(string.Empty, Assert.Single(payloads));
        Assert.Empty(AdbDeviceListParser.Parse(payloads[0]));
    }

    [Fact]
    public void Append_SkipsDaemonMessageBeforeFrames()
    {
        var text = "* daemon not running; starting now at tcp:5037\n* daemon started successfully\n" + Frame("TESTSERIAL01\tdevice\n");

        var payloads = new TrackDevicesFrameDecoder().Append(Bytes(text));

        Assert.Equal("TESTSERIAL01\tdevice\n", Assert.Single(payloads));
    }

    [Fact]
    public void Append_IncompleteHeader_WaitsForMoreData()
    {
        var decoder = new TrackDevicesFrameDecoder();

        Assert.Empty(decoder.Append(Bytes("00")));
        Assert.Empty(decoder.Append(Bytes("05ABC")));
        Assert.Equal("ABCDE", Assert.Single(decoder.Append(Bytes("DE"))));
    }

    [Fact]
    public void Append_InvalidHeader_Throws()
    {
        Assert.Throws<AdbException>(() => new TrackDevicesFrameDecoder().Append(Bytes("zzzzTESTSERIAL01\tdevice\n")));
    }

    [Fact]
    public void DecoderAndParser_EndToEnd()
    {
        var payload =
            "TESTSERIAL01           device usb:1-1 product:testprod model:Test_Phone device:testdev transport_id:3\n" +
            "192.0.2.10:5555        unauthorized transport_id:4\n";

        var payloads = new TrackDevicesFrameDecoder().Append(Bytes(Frame(payload)));
        var devices = AdbDeviceListParser.Parse(Assert.Single(payloads));

        Assert.Equal(2, devices.Count);
        Assert.Equal(AdbTransport.Usb, devices[0].Transport);
        Assert.Equal(AdbDeviceState.Unauthorized, devices[1].State);
    }
}

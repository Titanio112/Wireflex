using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Decisions;
using Wireflex.Core.Tray;

namespace Wireflex.Tests.Tray;

/// <summary>Só nomes e seriais fictícios.</summary>
public sealed class TrayMenuModelTests
{
    private const string PhoneA = "TESTSERIAL01";
    private const string PhoneB = "TESTSERIAL02";
    private const string PhoneC = "TESTSERIAL03";
    private const string WifiA = "192.0.2.10:5555";

    private static readonly Dictionary<string, string> Names = new()
    {
        [PhoneA] = "Celular Alfa",
        [PhoneB] = "Celular Beta",
        [PhoneC] = "Celular Gama",
    };

    [Theory]
    [InlineData(PhonePhase.Mirroring, AdbTransport.Usb, AdbDeviceState.Device, TrayPhoneState.MirroringByCable)]
    [InlineData(PhonePhase.Mirroring, AdbTransport.WifiTcpip, AdbDeviceState.Device, TrayPhoneState.MirroringByWifi)]
    [InlineData(PhonePhase.OutOfRange, AdbTransport.WifiTcpip, AdbDeviceState.Offline, TrayPhoneState.Offline)]
    [InlineData(PhonePhase.Unavailable, AdbTransport.Usb, AdbDeviceState.Unauthorized, TrayPhoneState.Unauthorized)]
    [InlineData(PhonePhase.Unavailable, AdbTransport.Usb, AdbDeviceState.Offline, TrayPhoneState.Offline)]
    [InlineData(PhonePhase.Unavailable, AdbTransport.Usb, AdbDeviceState.Connecting, TrayPhoneState.Connecting)]
    [InlineData(PhonePhase.ClosedByUser, AdbTransport.Usb, AdbDeviceState.Device, TrayPhoneState.ClosedByUser)]
    [InlineData(PhonePhase.Paused, AdbTransport.Usb, AdbDeviceState.Device, TrayPhoneState.Paused)]
    [InlineData(PhonePhase.WaitingForAnswer, AdbTransport.Usb, AdbDeviceState.Device, TrayPhoneState.WaitingForAnswer)]
    [InlineData(PhonePhase.Idle, AdbTransport.Usb, AdbDeviceState.Device, TrayPhoneState.Available)]
    public void Estado_VemDoMotor(PhonePhase phase, AdbTransport transport, AdbDeviceState state, TrayPhoneState expected)
    {
        var adbSerial = transport == AdbTransport.Usb ? PhoneA : WifiA;
        var status = new PhoneStatus(
            PhoneA,
            phase,
            phase == PhonePhase.Mirroring ? adbSerial : null,
            phase == PhonePhase.Mirroring ? transport : null,
            [new PhoneConnectionStatus(adbSerial, transport, state)],
            phase == PhonePhase.Paused);

        Assert.Equal(expected, TrayMenuModel.StateOf(status));
    }

    [Fact]
    public void ConhecidoQueNaoApareceu_FicaForaDoAlcance()
    {
        var menu = TrayMenuModel.Build([], [new KnownPhone { Serial = PhoneB }], Name);

        var phone = Assert.Single(menu.Phones);
        Assert.Equal(("Celular Beta", TrayPhoneState.OutOfRange), (phone.Name, phone.State));
    }

    [Fact]
    public void CelularSoDoMotor_TambemAparece()
    {
        // Ex: só pelo Wi-Fi, ainda sem entrada na config.
        var menu = TrayMenuModel.Build([Mirroring(PhoneC, AdbTransport.WifiTcpip)], [], Name);

        Assert.Equal(TrayPhoneState.MirroringByWifi, Assert.Single(menu.Phones).State);
    }

    [Fact]
    public void Lista_SemRepetirEEmOrdemDeNome()
    {
        var menu = TrayMenuModel.Build(
            [Mirroring(PhoneC, AdbTransport.Usb), Mirroring(PhoneA, AdbTransport.WifiTcpip)],
            [new KnownPhone { Serial = PhoneA }, new KnownPhone { Serial = PhoneB }],
            Name);

        Assert.Equal(["Celular Alfa", "Celular Beta", "Celular Gama"], menu.Phones.Select(p => p.Name));
    }

    [Theory]
    [InlineData(PhonePhase.ClosedByUser, true)]
    [InlineData(PhonePhase.Idle, true)]
    [InlineData(PhonePhase.WaitingForAnswer, true)]
    [InlineData(PhonePhase.Mirroring, false)]
    [InlineData(PhonePhase.Paused, false)]
    [InlineData(PhonePhase.OutOfRange, false)]
    [InlineData(PhonePhase.Ignored, false)]
    public void AbrirAgora_SoComOCelularAoAlcanceESemNadaAberto(PhonePhase phase, bool expected)
    {
        var menu = TrayMenuModel.Build([Status(PhoneA, phase)], [], Name);

        Assert.Equal(expected, Assert.Single(menu.Phones).CanOpenNow);
    }

    [Fact]
    public void Desativado_ApareceComoDesativadoESemAbrirAgora()
    {
        var known = new KnownPhone { Serial = PhoneA, Settings = new PhoneSettings { ConnectionMode = ConnectionMode.Never } };

        var phone = Assert.Single(TrayMenuModel.Build([Status(PhoneA, PhonePhase.Idle)], [known], Name).Phones);

        Assert.Equal((TrayPhoneState.Disabled, true, false), (phone.State, phone.Disabled, phone.CanOpenNow));
    }

    [Fact]
    public void ModoGlobalNunca_SemAbrirAgora()
    {
        var phone = Assert.Single(TrayMenuModel.Build([Status(PhoneA, PhonePhase.Idle)], [], Name, ConnectionMode.Never).Phones);

        Assert.False(phone.CanOpenNow);
    }

    [Fact]
    public void Esquecido_NaoPausaNemMudaAjustes()
    {
        var phone = Assert.Single(TrayMenuModel.Build([Status(PhoneA, PhonePhase.Ignored)], [], Name).Phones);

        Assert.Equal(TrayPhoneState.Forgotten, phone.State);
        Assert.False(phone.CanPause);
        Assert.False(phone.CanChangeSettings);
    }

    [Fact]
    public void ForaDoAlcance_NaoPausa()
    {
        var phone = Assert.Single(TrayMenuModel.Build([], [new KnownPhone { Serial = PhoneA }], Name).Phones);

        Assert.False(phone.CanPause);
        Assert.True(phone.CanChangeSettings);
    }

    [Fact]
    public void Tooltip_ResumoCurto()
    {
        var menu = TrayMenuModel.Build(
            [Mirroring(PhoneA, AdbTransport.Usb), Mirroring(PhoneB, AdbTransport.WifiTcpip)],
            [new KnownPhone { Serial = PhoneC }],
            Name);

        Assert.Equal("Wireflex: 2 espelhando, 1 fora do alcance", menu.Tooltip);
    }

    [Fact]
    public void Tooltip_SemCelular()
    {
        Assert.Equal("Wireflex: nenhum celular", TrayMenuModel.Build([], [], Name).Tooltip);
    }

    [Fact]
    public void Tooltip_PluralENoLimite()
    {
        var phones = Enumerable.Range(0, 200)
            .Select(i => new TrayPhone($"TESTSERIAL{i:000}", "Celular", (TrayPhoneState)(i % 10), "x"))
            .ToList();

        var tooltip = TrayMenuModel.Tooltip(phones);

        Assert.Contains("disponíveis", tooltip);
        Assert.True(tooltip.Length <= TrayMenuModel.TooltipMaxLength);
    }

    private static string Name(string serial) => Names[serial];

    private static PhoneStatus Status(string serial, PhonePhase phase)
    {
        var state = phase == PhonePhase.OutOfRange ? AdbDeviceState.Offline : AdbDeviceState.Device;
        return new PhoneStatus(
            serial,
            phase,
            phase == PhonePhase.Mirroring ? serial : null,
            phase == PhonePhase.Mirroring ? AdbTransport.Usb : null,
            [new PhoneConnectionStatus(serial, AdbTransport.Usb, state)],
            phase == PhonePhase.Paused);
    }

    private static PhoneStatus Mirroring(string serial, AdbTransport transport)
    {
        var adbSerial = transport == AdbTransport.Usb ? serial : WifiA;
        return new PhoneStatus(serial, PhonePhase.Mirroring, adbSerial, transport, [new PhoneConnectionStatus(adbSerial, transport, AdbDeviceState.Device)], false);
    }
}

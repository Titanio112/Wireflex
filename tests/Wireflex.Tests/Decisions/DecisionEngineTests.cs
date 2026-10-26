using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Decisions;

namespace Wireflex.Tests.Decisions;

public class DecisionEngineTests
{
    private const string Phone1 = "TESTSERIAL01";
    private const string Phone2 = "TESTSERIAL02";
    private const string Wifi1 = "192.0.2.10:5555";
    private const string Wifi2 = "192.0.2.11:5555";

    private readonly WireflexConfig _config = new();
    private readonly DecisionEngine _engine;

    public DecisionEngineTests()
    {
        _engine = new DecisionEngine(serial => _config.ResolveConnectionMode(serial));
    }

    private IReadOnlyList<EngineAction> Handle(EngineInput input) => _engine.Handle(input);

    private IReadOnlyList<EngineAction> PlugCable(string serial, AdbDeviceState state = AdbDeviceState.Device) =>
        Handle(new ConnectionAdded(serial, serial, AdbTransport.Usb, state));

    private IReadOnlyList<EngineAction> UnplugCable(string serial) => Handle(new ConnectionRemoved(serial, serial));

    private IReadOnlyList<EngineAction> WifiOn(string serial, string adbSerial, AdbDeviceState state = AdbDeviceState.Device) =>
        Handle(new ConnectionAdded(serial, adbSerial, AdbTransport.WifiTcpip, state));

    private IReadOnlyList<EngineAction> WifiOff(string serial, string adbSerial) => Handle(new ConnectionRemoved(serial, adbSerial));

    private static T Only<T>(IReadOnlyList<EngineAction> actions)
        where T : EngineAction => Assert.IsType<T>(Assert.Single(actions));

    private void SetPhoneMode(string serial, ConnectionMode mode) =>
        _config.Phones.Add(new KnownPhone { Serial = serial, Settings = new PhoneSettings { ConnectionMode = mode } });

    private PhoneStatus Status(string serial) => _engine.GetStatus().Single(p => p.Serial == serial);

    [Fact]
    public void PlugCable_OpensScrcpyViaUsb()
    {
        var open = Only<OpenScrcpy>(PlugCable(Phone1));

        Assert.Equal(Phone1, open.Serial);
        Assert.Equal(Phone1, open.AdbSerial);
        Assert.Equal(AdbTransport.Usb, open.Transport);
        Assert.Equal(PhonePhase.Mirroring, Status(Phone1).Phase);
    }

    [Fact]
    public void UnplugCableWithWifiActive_SwitchesToWifi()
    {
        PlugCable(Phone1);
        Only<DoNothing>(WifiOn(Phone1, Wifi1));

        var change = Only<SwitchScrcpy>(UnplugCable(Phone1));

        Assert.Equal(Wifi1, change.AdbSerial);
        Assert.Equal(AdbTransport.Usb, change.From);
        Assert.Equal(AdbTransport.WifiTcpip, change.To);
        Assert.Equal(AdbTransport.WifiTcpip, Status(Phone1).ScrcpyTransport);
    }

    [Fact]
    public void UnplugCableWithoutWifi_PhoneIsOutOfRange()
    {
        PlugCable(Phone1);

        Only<DoNothing>(UnplugCable(Phone1));

        Assert.Equal(PhonePhase.OutOfRange, Status(Phone1).Phase);
        Assert.Null(Status(Phone1).ScrcpyTransport);
    }

    [Fact]
    public void WifiActiveThenPlugCable_CableHasPriority()
    {
        Only<OpenScrcpy>(WifiOn(Phone1, Wifi1));

        var change = Only<SwitchScrcpy>(PlugCable(Phone1));

        Assert.Equal(Phone1, change.AdbSerial);
        Assert.Equal(AdbTransport.WifiTcpip, change.From);
        Assert.Equal(AdbTransport.Usb, change.To);
    }

    [Fact]
    public void WifiActiveThenUnauthorizedCable_StaysOnWifi()
    {
        WifiOn(Phone1, Wifi1);

        Only<DoNothing>(PlugCable(Phone1, AdbDeviceState.Unauthorized));

        Assert.Equal(AdbTransport.WifiTcpip, Status(Phone1).ScrcpyTransport);
    }

    [Fact]
    public void OldScrcpyExitAfterSwitch_IsIgnored()
    {
        PlugCable(Phone1);
        WifiOn(Phone1, Wifi1);
        Only<SwitchScrcpy>(UnplugCable(Phone1));

        // O scrcpy antigo (USB) termina depois da troca: não pode mexer no novo.
        Only<DoNothing>(Handle(new ScrcpyExited(Phone1, Phone1)));
        Only<DoNothing>(Handle(new ScrcpyClosedByUser(Phone1, Phone1)));

        Assert.Equal(PhonePhase.Mirroring, Status(Phone1).Phase);
    }

    [Fact]
    public void UserClosesScrcpy_DoesNotReopenWhileInRange()
    {
        PlugCable(Phone1);
        WifiOn(Phone1, Wifi1);

        Only<DoNothing>(Handle(new ScrcpyClosedByUser(Phone1, Phone1)));
        Assert.Equal(PhonePhase.ClosedByUser, Status(Phone1).Phase);

        // Continua ao alcance pelo Wi-Fi: tirar e plugar o cabo, mudar estado ou reavaliar não reabre.
        Only<DoNothing>(UnplugCable(Phone1));
        Only<DoNothing>(PlugCable(Phone1));
        Only<DoNothing>(Handle(new ConnectionStateChanged(Phone1, Wifi1, AdbDeviceState.Device)));
        Only<DoNothing>(Handle(new Reevaluate(null, "teste")));

        Assert.Equal(PhonePhase.ClosedByUser, Status(Phone1).Phase);
    }

    [Fact]
    public void ScrcpyExitsByItself_DoesNotReopenOnSameEvent_ButReopensOnNextEvent()
    {
        PlugCable(Phone1);

        var exited = Only<DoNothing>(Handle(new ScrcpyExited(Phone1, Phone1)));
        Assert.Contains("sozinho", exited.Reason, StringComparison.Ordinal);
        Assert.Equal(PhonePhase.Idle, Status(Phone1).Phase);

        // Não foi o usuário: o próximo evento (ex: adb voltou) pode reabrir.
        var open = Only<OpenScrcpy>(Handle(new Reevaluate(null, "servidor do adb disponível")));
        Assert.Equal(AdbTransport.Usb, open.Transport);
    }

    [Fact]
    public void ScrcpyExitsByItself_ReopensWhenWifiArrives()
    {
        PlugCable(Phone1);
        Handle(new ScrcpyExited(Phone1, Phone1));

        // Chega o Wi-Fi, mas o cabo (ainda ok) tem prioridade.
        var open = Only<OpenScrcpy>(WifiOn(Phone1, Wifi1));

        Assert.Equal(AdbTransport.Usb, open.Transport);
    }

    [Fact]
    public void LeaveRangeAndComeBack_OpensAgainAfterUserClosed()
    {
        PlugCable(Phone1);
        WifiOn(Phone1, Wifi1);
        Handle(new ScrcpyClosedByUser(Phone1, Phone1));

        UnplugCable(Phone1);
        Only<DoNothing>(WifiOff(Phone1, Wifi1));
        Assert.Equal(PhonePhase.OutOfRange, Status(Phone1).Phase);

        var open = Only<OpenScrcpy>(WifiOn(Phone1, Wifi1));
        Assert.Equal(AdbTransport.WifiTcpip, open.Transport);
    }

    [Fact]
    public void WifiGoingOfflineCountsAsOutOfRange()
    {
        WifiOn(Phone1, Wifi1);
        Handle(new ScrcpyClosedByUser(Phone1, Wifi1));

        // Celular saiu da rede: o adb mantém a conexão como offline.
        var notice = Only<NotifyUser>(Handle(new ConnectionStateChanged(Phone1, Wifi1, AdbDeviceState.Offline)));
        Assert.Equal(DeviceNotice.Offline, notice.Notice);
        Assert.Equal(PhonePhase.OutOfRange, Status(Phone1).Phase);

        // Voltou para a rede: conta como "saiu e voltou".
        Only<OpenScrcpy>(Handle(new ConnectionStateChanged(Phone1, Wifi1, AdbDeviceState.Device)));
    }

    [Fact]
    public void Unauthorized_DoesNotOpen_OnlyNotifiesOnce()
    {
        var notice = Only<NotifyUser>(PlugCable(Phone1, AdbDeviceState.Unauthorized));
        Assert.Equal(DeviceNotice.Unauthorized, notice.Notice);
        Assert.Equal(PhonePhase.Unavailable, Status(Phone1).Phase);

        // Mesmo estado de novo: não repete o aviso.
        Only<DoNothing>(Handle(new ConnectionStateChanged(Phone1, Phone1, AdbDeviceState.Unauthorized)));

        // Usuário autorizou no celular.
        Only<OpenScrcpy>(Handle(new ConnectionStateChanged(Phone1, Phone1, AdbDeviceState.Device)));
    }

    [Fact]
    public void Offline_DoesNotOpen_OnlyNotifies()
    {
        var notice = Only<NotifyUser>(PlugCable(Phone1, AdbDeviceState.Offline));

        Assert.Equal(DeviceNotice.Offline, notice.Notice);
        Assert.DoesNotContain(_engine.GetStatus(), p => p.ScrcpyTransport is not null);
    }

    [Fact]
    public void UnauthorizedWhileMirroring_StopsTrackingScrcpyAndNotifies()
    {
        PlugCable(Phone1);

        var notice = Only<NotifyUser>(Handle(new ConnectionStateChanged(Phone1, Phone1, AdbDeviceState.Unauthorized)));

        Assert.Equal(DeviceNotice.Unauthorized, notice.Notice);
        Assert.Null(Status(Phone1).ScrcpyTransport);
    }

    [Fact]
    public void AuthorizingOrConnecting_WaitsWithoutNotice()
    {
        Only<DoNothing>(PlugCable(Phone1, AdbDeviceState.Authorizing));
        Only<DoNothing>(WifiOn(Phone1, Wifi1, AdbDeviceState.Connecting));
    }

    [Fact]
    public void GlobalNeverMode_DoesNotOpenOrNotify()
    {
        _config.Global.ConnectionMode = ConnectionMode.Never;

        var nothing = Only<DoNothing>(PlugCable(Phone1));
        Assert.Contains("nunca", nothing.Reason, StringComparison.Ordinal);

        Only<DoNothing>(PlugCable(Phone2, AdbDeviceState.Unauthorized));
    }

    [Fact]
    public void GlobalAskMode_AsksOnce_ThenOpensOnYes()
    {
        _config.Global.ConnectionMode = ConnectionMode.Ask;

        var ask = Only<AskUser>(PlugCable(Phone1));
        Assert.Equal(AdbTransport.Usb, ask.Transport);
        Assert.Equal(PhonePhase.WaitingForAnswer, Status(Phone1).Phase);

        // Outro evento enquanto espera: não pergunta de novo.
        Only<DoNothing>(WifiOn(Phone1, Wifi1));

        var open = Only<OpenScrcpy>(Handle(new UserAnswered(Phone1, true)));
        Assert.Equal(AdbTransport.Usb, open.Transport);
    }

    [Fact]
    public void AskMode_No_SuppressesUntilLeavingRange()
    {
        _config.Global.ConnectionMode = ConnectionMode.Ask;
        PlugCable(Phone1);

        Only<DoNothing>(Handle(new UserAnswered(Phone1, false)));
        Assert.Equal(PhonePhase.ClosedByUser, Status(Phone1).Phase);
        Only<DoNothing>(Handle(new Reevaluate(Phone1, "teste")));

        UnplugCable(Phone1);
        Only<AskUser>(PlugCable(Phone1));
    }

    [Fact]
    public void AnswerWithoutQuestion_DoesNothing()
    {
        PlugCable(Phone1);

        Only<DoNothing>(Handle(new UserAnswered(Phone1, true)));
        Only<DoNothing>(Handle(new UserAnswered(Phone2, true)));
    }

    [Fact]
    public void PhoneNeverOverridesGlobalAutomatic()
    {
        SetPhoneMode(Phone1, ConnectionMode.Never);

        Only<DoNothing>(PlugCable(Phone1));
        Only<OpenScrcpy>(PlugCable(Phone2));
    }

    [Fact]
    public void PhoneAskOverridesGlobalAutomatic()
    {
        SetPhoneMode(Phone1, ConnectionMode.Ask);

        Only<AskUser>(PlugCable(Phone1));
        Only<OpenScrcpy>(PlugCable(Phone2));
    }

    [Fact]
    public void PhoneAutomaticOverridesGlobalNever()
    {
        _config.Global.ConnectionMode = ConnectionMode.Never;
        SetPhoneMode(Phone1, ConnectionMode.Automatic);

        Only<OpenScrcpy>(PlugCable(Phone1));
        Only<DoNothing>(PlugCable(Phone2));
    }

    [Fact]
    public void ModeChangeThenReevaluate_OpensPhone()
    {
        _config.Global.ConnectionMode = ConnectionMode.Never;
        PlugCable(Phone1);

        _config.Global.ConnectionMode = ConnectionMode.Automatic;

        Only<OpenScrcpy>(Handle(new Reevaluate(Phone1, "configuração mudou")));
    }

    [Fact]
    public void PausingWhileMirroring_ClosesScrcpy_AndResumingReopens()
    {
        PlugCable(Phone1);

        Only<CloseScrcpy>(Handle(new PhonePauseChanged(Phone1, true)));
        Assert.Equal(PhonePhase.Paused, Status(Phone1).Phase);

        // Pausado: eventos de conexão não abrem nem avisam.
        Only<DoNothing>(WifiOn(Phone1, Wifi1));
        Only<DoNothing>(Handle(new ConnectionStateChanged(Phone1, Phone1, AdbDeviceState.Unauthorized)));
        Handle(new ConnectionStateChanged(Phone1, Phone1, AdbDeviceState.Device));

        Only<OpenScrcpy>(Handle(new PhonePauseChanged(Phone1, false)));
    }

    [Fact]
    public void PausedBeforeConnecting_StaysPausedAcrossRange()
    {
        Handle(new PhonePauseChanged(Phone1, true));

        Only<DoNothing>(PlugCable(Phone1));
        UnplugCable(Phone1);
        Only<DoNothing>(PlugCable(Phone1));

        Assert.True(Status(Phone1).Paused);
    }

    [Fact]
    public void TwoPhones_HaveIndependentStates()
    {
        Only<OpenScrcpy>(PlugCable(Phone1));
        Only<OpenScrcpy>(WifiOn(Phone2, Wifi2));

        Handle(new ScrcpyClosedByUser(Phone1, Phone1));
        Only<NotifyUser>(Handle(new ConnectionStateChanged(Phone2, Wifi2, AdbDeviceState.Offline)));

        Assert.Equal(PhonePhase.ClosedByUser, Status(Phone1).Phase);
        Assert.Equal(PhonePhase.OutOfRange, Status(Phone2).Phase);

        // Phone2 volta e abre; Phone1 continua fechado pelo usuário.
        Only<OpenScrcpy>(Handle(new ConnectionStateChanged(Phone2, Wifi2, AdbDeviceState.Device)));
        Only<DoNothing>(Handle(new Reevaluate(Phone1, "teste")));

        var all = Handle(new Reevaluate(null, "teste"));
        Assert.Equal(2, all.Count);
        Assert.All(all, a => Assert.IsType<DoNothing>(a));
    }

    [Fact]
    public void TwoPhones_DifferentModes()
    {
        SetPhoneMode(Phone2, ConnectionMode.Ask);

        Only<OpenScrcpy>(PlugCable(Phone1));
        Only<AskUser>(PlugCable(Phone2));

        Only<DoNothing>(Handle(new UserAnswered(Phone2, false)));
        Assert.Equal(PhonePhase.Mirroring, Status(Phone1).Phase);
        Assert.Equal(PhonePhase.ClosedByUser, Status(Phone2).Phase);
    }

    [Fact]
    public void ScrcpyStartedManually_IsTracked()
    {
        _config.Global.ConnectionMode = ConnectionMode.Never;
        PlugCable(Phone1);

        Only<DoNothing>(Handle(new ScrcpyStarted(Phone1, Phone1)));

        Assert.Equal(PhonePhase.Mirroring, Status(Phone1).Phase);
        Assert.Equal(Phone1, Status(Phone1).ScrcpyAdbSerial);
    }

    [Fact]
    public void OpenRequested_AfterUserClosed_Opens()
    {
        PlugCable(Phone1);
        Handle(new ScrcpyClosedByUser(Phone1, Phone1));

        var open = Only<OpenScrcpy>(Handle(new OpenRequested(Phone1)));

        Assert.Equal(AdbTransport.Usb, open.Transport);
        Assert.Equal(PhonePhase.Mirroring, Status(Phone1).Phase);
    }

    [Fact]
    public void OpenRequested_InAskMode_OpensWithoutAsking()
    {
        _config.Global.ConnectionMode = ConnectionMode.Ask;
        Only<AskUser>(PlugCable(Phone1));

        Only<OpenScrcpy>(Handle(new OpenRequested(Phone1)));
        Assert.Equal(PhonePhase.Mirroring, Status(Phone1).Phase);
    }

    [Fact]
    public void OpenRequested_RefusedWhenPausedDisabledOrOutOfRange()
    {
        Only<DoNothing>(Handle(new OpenRequested(Phone1)));

        PlugCable(Phone1);
        Handle(new PhonePauseChanged(Phone1, true));
        Only<DoNothing>(Handle(new OpenRequested(Phone1)));

        SetPhoneMode(Phone2, ConnectionMode.Never);
        PlugCable(Phone2);
        Only<DoNothing>(Handle(new OpenRequested(Phone2)));
    }

    [Fact]
    public void DisablingWhileMirroring_ClosesScrcpy_AndEnablingReopens()
    {
        PlugCable(Phone1);
        SetPhoneMode(Phone1, ConnectionMode.Never);

        Only<CloseScrcpy>(Handle(new Reevaluate(Phone1, "celular desativado")));

        _config.Phones.Clear();
        Only<OpenScrcpy>(Handle(new Reevaluate(Phone1, "celular ativado")));
    }

    [Fact]
    public void ForgottenWhileConnected_ClosesAndStaysIgnoredUntilOutOfRange()
    {
        PlugCable(Phone1);
        WifiOn(Phone1, Wifi1);

        Only<CloseScrcpy>(Handle(new PhoneForgotten(Phone1)));
        Assert.Equal(PhonePhase.Ignored, Status(Phone1).Phase);

        // Ainda ao alcance: nada abre sozinho, nem avisa.
        Only<DoNothing>(UnplugCable(Phone1));
        Only<DoNothing>(Handle(new ConnectionStateChanged(Phone1, Wifi1, AdbDeviceState.Unauthorized)));
        Only<DoNothing>(Handle(new ConnectionStateChanged(Phone1, Wifi1, AdbDeviceState.Device)));
        Only<DoNothing>(Handle(new Reevaluate(null, "teste")));
        Only<DoNothing>(Handle(new OpenRequested(Phone1)));

        // Sai do alcance e volta: é um celular como outro qualquer.
        WifiOff(Phone1, Wifi1);
        Only<OpenScrcpy>(PlugCable(Phone1));
    }

    [Fact]
    public void ForgottenBeforeConnectionsArrive_StaysIgnored()
    {
        // O coordenador segurava os eventos: o motor recebe o "esquecido" antes das conexões.
        Only<DoNothing>(Handle(new PhoneForgotten(Phone1)));

        Only<DoNothing>(PlugCable(Phone1));
        Assert.Equal(PhonePhase.Ignored, Status(Phone1).Phase);
    }

    [Fact]
    public void UnknownInputs_DoNothing()
    {
        Only<DoNothing>(UnplugCable(Phone1));
        Only<DoNothing>(Handle(new ScrcpyClosedByUser(Phone1, Phone1)));
        Only<DoNothing>(Handle(new Reevaluate(Phone1, "teste")));
        Only<DoNothing>(Handle(new Reevaluate(null, "teste")));
    }
}

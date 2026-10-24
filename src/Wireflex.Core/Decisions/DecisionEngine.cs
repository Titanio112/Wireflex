using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;

namespace Wireflex.Core.Decisions;

/// <summary>
/// Máquina de estados por celular. Recebe <see cref="EngineInput"/> e devolve as <see cref="EngineAction"/>
/// a executar. Lógica pura e determinística: não chama adb, não abre processos, não lê relógio.
/// Não é thread-safe; quem usa deve chamar <see cref="Handle"/> de um lugar só.
/// </summary>
/// <remarks>
/// Regras:
/// <list type="number">
/// <item>Cabo tem prioridade: com o scrcpy no Wi-Fi, plugar o cabo troca para USB.</item>
/// <item>Se o usuário fechou o scrcpy, não reabre até o celular sair do alcance (sem USB e sem Wi-Fi) e voltar.
/// Conexão só "offline" conta como fora do alcance.</item>
/// <item>Modo de conexão automático / perguntar / nunca, com a configuração do celular sobrepondo a global.</item>
/// <item>unauthorized ou offline: não tenta abrir, só avisa (uma vez por ocorrência).</item>
/// <item>Cada celular tem o seu estado, independente dos outros.</item>
/// </list>
/// </remarks>
public sealed class DecisionEngine
{
    private readonly Func<string, ConnectionMode> _resolveConnectionMode;
    private readonly Dictionary<string, PhoneSession> _phones = new(StringComparer.Ordinal);

    /// <param name="resolveConnectionMode">Modo de conexão efetivo de um celular (config do celular ou global).</param>
    public DecisionEngine(Func<string, ConnectionMode> resolveConnectionMode)
    {
        _resolveConnectionMode = resolveConnectionMode ?? throw new ArgumentNullException(nameof(resolveConnectionMode));
    }

    public IReadOnlyList<EngineAction> Handle(EngineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return input switch
        {
            ConnectionAdded e => UpsertLink(e.Serial, e.AdbSerial, e.Transport, e.State),
            ConnectionStateChanged e => UpsertLink(e.Serial, e.AdbSerial, AdbSerialClassifier.Classify(e.AdbSerial), e.State),
            ConnectionRemoved e => OnConnectionRemoved(e),
            ScrcpyStarted e => OnScrcpyStarted(e),
            ScrcpyClosedByUser e => OnScrcpyClosedByUser(e),
            ScrcpyExited e => OnScrcpyExited(e),
            PhonePauseChanged e => OnPauseChanged(e),
            UserAnswered e => OnUserAnswered(e),
            Reevaluate e => OnReevaluate(e),
            _ => [new DoNothing(null, $"entrada desconhecida: {input.GetType().Name}")],
        };
    }

    public IReadOnlyList<PhoneStatus> GetStatus() =>
        _phones.Values
            .OrderBy(p => p.Serial, StringComparer.Ordinal)
            .Select(p => new PhoneStatus(
                p.Serial,
                p.Phase,
                p.ScrcpyTransport,
                p.Links.Values
                    .OrderBy(l => AdbTransports.Priority(l.Transport))
                    .Select(l => new PhoneConnectionStatus(l.AdbSerial, l.Transport, l.State))
                    .ToList(),
                p.Paused))
            .ToList();

    private IReadOnlyList<EngineAction> UpsertLink(string serial, string adbSerial, AdbTransport transport, AdbDeviceState state)
    {
        var phone = GetOrCreate(serial);
        phone.Links[adbSerial] = new Link(adbSerial, transport, state);
        return Evaluate(phone, allowOpen: true);
    }

    private IReadOnlyList<EngineAction> OnConnectionRemoved(ConnectionRemoved e)
    {
        if (!_phones.TryGetValue(e.Serial, out var phone) || !phone.Links.Remove(e.AdbSerial))
        {
            return [new DoNothing(e.Serial, "conexão desconhecida")];
        }

        return Evaluate(phone, allowOpen: true);
    }

    private IReadOnlyList<EngineAction> OnScrcpyStarted(ScrcpyStarted e)
    {
        var phone = GetOrCreate(e.Serial);
        var transport = phone.Links.TryGetValue(e.AdbSerial, out var link)
            ? link.Transport
            : AdbSerialClassifier.Classify(e.AdbSerial);
        phone.SetScrcpy(e.AdbSerial, transport);
        phone.AwaitingAnswer = false;
        phone.ClosedByUser = false;
        return [new DoNothing(e.Serial, $"scrcpy aberto via {transport}")];
    }

    private IReadOnlyList<EngineAction> OnScrcpyClosedByUser(ScrcpyClosedByUser e)
    {
        if (!_phones.TryGetValue(e.Serial, out var phone) || phone.ScrcpyAdbSerial != e.AdbSerial)
        {
            return [new DoNothing(e.Serial, "evento de um scrcpy que não está mais em uso; ignorado")];
        }

        phone.ClearScrcpy();
        if (!phone.InRange)
        {
            return [new DoNothing(e.Serial, "scrcpy fechado com o celular fora do alcance")];
        }

        phone.ClosedByUser = true;
        return [new DoNothing(e.Serial, "fechado pelo usuário; só reabre quando o celular sair do alcance e voltar")];
    }

    private IReadOnlyList<EngineAction> OnScrcpyExited(ScrcpyExited e)
    {
        if (!_phones.TryGetValue(e.Serial, out var phone) || phone.ScrcpyAdbSerial != e.AdbSerial)
        {
            return [new DoNothing(e.Serial, "evento de um scrcpy que não está mais em uso; ignorado")];
        }

        phone.ClearScrcpy();
        return Evaluate(phone, allowOpen: false);
    }

    private IReadOnlyList<EngineAction> OnPauseChanged(PhonePauseChanged e)
    {
        var phone = GetOrCreate(e.Serial);
        phone.Paused = e.Paused;
        return Evaluate(phone, allowOpen: true);
    }

    private IReadOnlyList<EngineAction> OnUserAnswered(UserAnswered e)
    {
        if (!_phones.TryGetValue(e.Serial, out var phone) || !phone.AwaitingAnswer)
        {
            return [new DoNothing(e.Serial, "nenhuma pergunta pendente")];
        }

        phone.AwaitingAnswer = false;
        if (!e.Accepted)
        {
            phone.ClosedByUser = true;
            return [new DoNothing(e.Serial, "usuário recusou; só pergunta de novo quando o celular sair do alcance e voltar")];
        }

        if (phone.Best is { } best && !phone.Paused && !phone.ScrcpyRunning)
        {
            phone.SetScrcpy(best.AdbSerial, best.Transport);
            return [new OpenScrcpy(phone.Serial, best.AdbSerial, best.Transport)];
        }

        return Evaluate(phone, allowOpen: true);
    }

    private IReadOnlyList<EngineAction> OnReevaluate(Reevaluate e)
    {
        if (e.Serial is not null)
        {
            return _phones.TryGetValue(e.Serial, out var phone)
                ? Evaluate(phone, allowOpen: true)
                : [new DoNothing(e.Serial, "celular desconhecido")];
        }

        var actions = _phones.Values
            .OrderBy(p => p.Serial, StringComparer.Ordinal)
            .SelectMany(p => Evaluate(p, allowOpen: true))
            .ToList();
        return actions.Count > 0 ? actions : [new DoNothing(null, $"nenhum celular para reavaliar ({e.Reason})")];
    }

    private IReadOnlyList<EngineAction> Evaluate(PhoneSession phone, bool allowOpen)
    {
        var serial = phone.Serial;

        if (!phone.InRange)
        {
            // Saiu do alcance: zera "fechado pelo usuário" e perguntas pendentes.
            phone.ClosedByUser = false;
            phone.AwaitingAnswer = false;
            phone.ClearScrcpy();

            if (phone.Links.Count == 0)
            {
                phone.LastNotice = null;
                return [new DoNothing(serial, "fora do alcance (sem USB e sem Wi-Fi)")];
            }

            return Notice(phone, DeviceNotice.Offline);
        }

        var best = phone.Best;
        if (best is null)
        {
            phone.ClearScrcpy();
            if (phone.Links.Values.Any(l => l.State == AdbDeviceState.Unauthorized))
            {
                return Notice(phone, DeviceNotice.Unauthorized);
            }

            if (phone.Links.Values.Any(l => l.State == AdbDeviceState.Offline))
            {
                return Notice(phone, DeviceNotice.Offline);
            }

            var states = string.Join(", ", phone.Links.Values.Select(l => l.State).Distinct());
            return [new DoNothing(serial, $"aguardando a conexão ficar pronta ({states})")];
        }

        phone.LastNotice = null;

        if (phone.Paused)
        {
            if (phone.ScrcpyRunning)
            {
                phone.ClearScrcpy();
                return [new CloseScrcpy(serial, "celular pausado")];
            }

            return [new DoNothing(serial, "celular pausado")];
        }

        if (phone.ScrcpyAdbSerial is { } currentAdbSerial)
        {
            var current = phone.Links.GetValueOrDefault(currentAdbSerial);
            var currentUsable = current?.State == AdbDeviceState.Device;
            var cableArrived = best.Transport == AdbTransport.Usb && current?.Transport != AdbTransport.Usb;
            if (currentUsable && !cableArrived)
            {
                return [new DoNothing(serial, $"scrcpy já aberto via {current!.Transport}")];
            }

            var from = phone.ScrcpyTransport ?? AdbSerialClassifier.Classify(currentAdbSerial);
            phone.SetScrcpy(best.AdbSerial, best.Transport);
            return [new SwitchScrcpy(serial, best.AdbSerial, from, best.Transport)];
        }

        if (phone.ClosedByUser)
        {
            return [new DoNothing(serial, "fechado pelo usuário; só reabre quando o celular sair do alcance e voltar")];
        }

        if (phone.AwaitingAnswer)
        {
            return [new DoNothing(serial, "aguardando a resposta do usuário")];
        }

        if (!allowOpen)
        {
            return [new DoNothing(serial, "scrcpy encerrou sozinho; não reabre agora")];
        }

        switch (_resolveConnectionMode(serial))
        {
            case ConnectionMode.Never:
                return [new DoNothing(serial, "modo de conexão: nunca")];
            case ConnectionMode.Ask:
                phone.AwaitingAnswer = true;
                return [new AskUser(serial, best.AdbSerial, best.Transport)];
            default:
                phone.SetScrcpy(best.AdbSerial, best.Transport);
                return [new OpenScrcpy(serial, best.AdbSerial, best.Transport)];
        }
    }

    private IReadOnlyList<EngineAction> Notice(PhoneSession phone, DeviceNotice notice)
    {
        if (phone.Paused)
        {
            return [new DoNothing(phone.Serial, $"celular pausado ({notice})")];
        }

        if (_resolveConnectionMode(phone.Serial) == ConnectionMode.Never)
        {
            return [new DoNothing(phone.Serial, $"modo de conexão: nunca ({notice})")];
        }

        if (phone.LastNotice == notice)
        {
            return [new DoNothing(phone.Serial, $"aviso já dado ({notice}); não tenta abrir")];
        }

        phone.LastNotice = notice;
        return [new NotifyUser(phone.Serial, notice)];
    }

    private PhoneSession GetOrCreate(string serial)
    {
        if (!_phones.TryGetValue(serial, out var phone))
        {
            phone = new PhoneSession(serial);
            _phones[serial] = phone;
        }

        return phone;
    }

    private sealed record Link(string AdbSerial, AdbTransport Transport, AdbDeviceState State);

    private sealed class PhoneSession(string serial)
    {
        public string Serial { get; } = serial;

        public Dictionary<string, Link> Links { get; } = new(StringComparer.Ordinal);

        public string? ScrcpyAdbSerial { get; private set; }

        public AdbTransport? ScrcpyTransport { get; private set; }

        public bool ClosedByUser { get; set; }

        public bool AwaitingAnswer { get; set; }

        public bool Paused { get; set; }

        public DeviceNotice? LastNotice { get; set; }

        public bool ScrcpyRunning => ScrcpyAdbSerial is not null;

        public bool InRange => Links.Values.Any(l => l.State != AdbDeviceState.Offline);

        public Link? Best => Links.Values
            .Where(l => l.State == AdbDeviceState.Device)
            .OrderBy(l => AdbTransports.Priority(l.Transport))
            .ThenBy(l => l.AdbSerial, StringComparer.Ordinal)
            .FirstOrDefault();

        public PhonePhase Phase =>
            !InRange ? PhonePhase.OutOfRange
            : Best is null ? PhonePhase.Unavailable
            : Paused ? PhonePhase.Paused
            : ScrcpyRunning ? PhonePhase.Mirroring
            : AwaitingAnswer ? PhonePhase.WaitingForAnswer
            : ClosedByUser ? PhonePhase.ClosedByUser
            : PhonePhase.Idle;

        public void SetScrcpy(string adbSerial, AdbTransport transport)
        {
            ScrcpyAdbSerial = adbSerial;
            ScrcpyTransport = transport;
        }

        public void ClearScrcpy()
        {
            ScrcpyAdbSerial = null;
            ScrcpyTransport = null;
        }
    }
}

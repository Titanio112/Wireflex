using Wireflex.Core.Adb;
using Wireflex.Core.Configuration;
using Wireflex.Core.Decisions;

namespace Wireflex.Core.Tray;

/// <summary>Como um celular aparece no menu da bandeja.</summary>
public enum TrayPhoneState
{
    MirroringByCable,
    MirroringByWifi,
    ClosedByUser,
    OutOfRange,
    Offline,
    Paused,
    WaitingForAnswer,
    Unauthorized,

    /// <summary>Ao alcance, conexão ainda não pronta.</summary>
    Connecting,

    /// <summary>Ao alcance, nada aberto (ex: modo nunca).</summary>
    Available,

    /// <summary>Modo nunca só para este celular.</summary>
    Disabled,

    /// <summary>Esquecido com o celular ainda ligado: ignorado até sair do alcance e voltar.</summary>
    Forgotten,
}

/// <param name="Name">Nome para o menu (o nome do aparelho pode ter o nome do dono: fica só na tela e no log local).</param>
/// <param name="Paused">Pausado nesta sessão (até fechar o Wireflex).</param>
/// <param name="Disabled">Modo nunca só para este celular, gravado na config.</param>
/// <param name="CanOpenNow">"Abrir agora" disponível: ao alcance, sem scrcpy aberto, nem pausado, desativado ou esquecido.</param>
/// <param name="CanPause">Pausar/retomar disponível (o celular foi visto nesta sessão e não foi esquecido).</param>
/// <param name="CanChangeSettings">Desativar e esquecer disponíveis (não para quem acabou de ser esquecido).</param>
public sealed record TrayPhone(
    string Serial,
    string Name,
    TrayPhoneState State,
    string StatusText,
    bool Paused = false,
    bool Disabled = false,
    bool CanOpenNow = false,
    bool CanPause = false,
    bool CanChangeSettings = true);

/// <param name="Tooltip">Resumo curto para o tooltip do ícone (no máximo <see cref="TrayMenuModel.TooltipMaxLength"/> caracteres).</param>
/// <param name="GlobalMode">Modo de conexão global, marcado no menu.</param>
public sealed record TrayMenu(IReadOnlyList<TrayPhone> Phones, string Tooltip, ConnectionMode GlobalMode = ConnectionMode.Automatic);

/// <summary>
/// Monta o conteúdo do menu da bandeja a partir do motor (celulares vistos nesta sessão) e da config (celulares
/// conhecidos, mesmo fora do alcance). Sem tela: a bandeja só desenha.
/// </summary>
public static class TrayMenuModel
{
    /// <summary>Limite do texto do tooltip do ícone na bandeja.</summary>
    public const int TooltipMaxLength = 127;

    // Ordem do resumo no tooltip, com singular e plural.
    private static readonly (TrayPhoneState[] States, string One, string Many)[] Summary =
    [
        ([TrayPhoneState.MirroringByCable, TrayPhoneState.MirroringByWifi], "espelhando", "espelhando"),
        ([TrayPhoneState.WaitingForAnswer], "aguardando resposta", "aguardando resposta"),
        ([TrayPhoneState.Available], "disponível", "disponíveis"),
        ([TrayPhoneState.ClosedByUser], "fechado", "fechados"),
        ([TrayPhoneState.Paused], "pausado", "pausados"),
        ([TrayPhoneState.Disabled], "desativado", "desativados"),
        ([TrayPhoneState.Forgotten], "esquecido", "esquecidos"),
        ([TrayPhoneState.Connecting], "conectando", "conectando"),
        ([TrayPhoneState.Unauthorized], "sem autorização", "sem autorização"),
        ([TrayPhoneState.Offline], "offline", "offline"),
        ([TrayPhoneState.OutOfRange], "fora do alcance", "fora do alcance"),
    ];

    /// <param name="globalMode">Modo de conexão global (vale para quem não tem um próprio).</param>
    public static TrayMenu Build(
        IReadOnlyList<PhoneStatus> engine,
        IReadOnlyList<KnownPhone> known,
        Func<string, string> displayName,
        ConnectionMode globalMode = ConnectionMode.Automatic)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(displayName);

        var bySerial = engine.ToDictionary(p => p.Serial, StringComparer.Ordinal);
        var settings = known.ToDictionary(p => p.Serial, p => p.Settings, StringComparer.Ordinal);
        var serials = known.Select(p => p.Serial).Concat(engine.Select(p => p.Serial)).Distinct(StringComparer.Ordinal);
        var phones = serials
            .Select(serial => Phone(
                serial,
                displayName(serial),
                bySerial.GetValueOrDefault(serial),
                settings.GetValueOrDefault(serial)?.ConnectionMode,
                globalMode))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Serial, StringComparer.Ordinal)
            .ToList();

        return new TrayMenu(phones, Tooltip(phones), globalMode);
    }

    public static string ModeText(ConnectionMode mode) => mode switch
    {
        ConnectionMode.Ask => "Perguntar antes de abrir",
        ConnectionMode.Never => "Nunca abrir",
        _ => "Automático",
    };

    private static TrayPhone Phone(string serial, string name, PhoneStatus? status, ConnectionMode? phoneMode, ConnectionMode globalMode)
    {
        var state = status is null ? TrayPhoneState.OutOfRange : StateOf(status);
        var forgotten = state == TrayPhoneState.Forgotten;
        var disabled = phoneMode == ConnectionMode.Never;
        if (disabled && !forgotten)
        {
            state = TrayPhoneState.Disabled;
        }

        var paused = status?.Paused ?? false;
        var canOpenNow = !paused
            && (phoneMode ?? globalMode) != ConnectionMode.Never
            && status is { Phase: PhonePhase.ClosedByUser or PhonePhase.Idle or PhonePhase.WaitingForAnswer };
        return new TrayPhone(
            serial,
            name,
            state,
            StatusText(state),
            paused,
            disabled,
            canOpenNow,
            CanPause: status is not null && !forgotten,
            CanChangeSettings: !forgotten);
    }

    public static TrayPhoneState StateOf(PhoneStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var offline = status.Connections.Any(c => c.State == AdbDeviceState.Offline);
        return status.Phase switch
        {
            PhonePhase.Mirroring => status.ScrcpyTransport == AdbTransport.Usb
                ? TrayPhoneState.MirroringByCable
                : TrayPhoneState.MirroringByWifi,
            PhonePhase.OutOfRange => offline ? TrayPhoneState.Offline : TrayPhoneState.OutOfRange,
            PhonePhase.Unavailable when status.Connections.Any(c => c.State == AdbDeviceState.Unauthorized) => TrayPhoneState.Unauthorized,
            PhonePhase.Unavailable => offline ? TrayPhoneState.Offline : TrayPhoneState.Connecting,
            PhonePhase.Paused => TrayPhoneState.Paused,
            PhonePhase.WaitingForAnswer => TrayPhoneState.WaitingForAnswer,
            PhonePhase.ClosedByUser => TrayPhoneState.ClosedByUser,
            PhonePhase.Ignored => TrayPhoneState.Forgotten,
            _ => TrayPhoneState.Available,
        };
    }

    public static string StatusText(TrayPhoneState state) => state switch
    {
        TrayPhoneState.MirroringByCable => "espelhando pelo cabo",
        TrayPhoneState.MirroringByWifi => "espelhando pelo Wi-Fi",
        TrayPhoneState.ClosedByUser => "janela fechada (só reabre ao sair do alcance e voltar)",
        TrayPhoneState.OutOfRange => "fora do alcance",
        TrayPhoneState.Offline => "offline (o adb vê, mas o celular não responde)",
        TrayPhoneState.Paused => "pausado",
        TrayPhoneState.WaitingForAnswer => "aguardando resposta: abrir o espelhamento?",
        TrayPhoneState.Unauthorized => "autorize a depuração USB no celular",
        TrayPhoneState.Connecting => "conectando",
        TrayPhoneState.Disabled => "desativado",
        TrayPhoneState.Forgotten => "esquecido (ignorado até sair do alcance e voltar)",
        _ => "disponível",
    };

    /// <summary>Resumo curto, ex: "Wireflex: 2 espelhando, 1 fora do alcance".</summary>
    public static string Tooltip(IReadOnlyCollection<TrayPhone> phones)
    {
        ArgumentNullException.ThrowIfNull(phones);
        if (phones.Count == 0)
        {
            return $"{AppInfo.Name}: nenhum celular";
        }

        var parts = Summary
            .Select(s => (s, Count: phones.Count(p => s.States.Contains(p.State))))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count} {(x.Count == 1 ? x.s.One : x.s.Many)}");
        var text = $"{AppInfo.Name}: {string.Join(", ", parts)}";
        return text.Length <= TooltipMaxLength ? text : text[..(TooltipMaxLength - 3)] + "...";
    }
}

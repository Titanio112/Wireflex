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
}

/// <param name="Name">Nome para o menu (o nome do aparelho pode ter o nome do dono: fica só na tela e no log local).</param>
public sealed record TrayPhone(string Serial, string Name, TrayPhoneState State, string StatusText);

/// <param name="Tooltip">Resumo curto para o tooltip do ícone (no máximo <see cref="TrayMenuModel.TooltipMaxLength"/> caracteres).</param>
public sealed record TrayMenu(IReadOnlyList<TrayPhone> Phones, string Tooltip);

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
        ([TrayPhoneState.Connecting], "conectando", "conectando"),
        ([TrayPhoneState.Unauthorized], "sem autorização", "sem autorização"),
        ([TrayPhoneState.Offline], "offline", "offline"),
        ([TrayPhoneState.OutOfRange], "fora do alcance", "fora do alcance"),
    ];

    public static TrayMenu Build(
        IReadOnlyList<PhoneStatus> engine,
        IReadOnlyList<KnownPhone> known,
        Func<string, string> displayName)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(displayName);

        var bySerial = engine.ToDictionary(p => p.Serial, StringComparer.Ordinal);
        var serials = known.Select(p => p.Serial).Concat(engine.Select(p => p.Serial)).Distinct(StringComparer.Ordinal);
        var phones = serials
            .Select(serial =>
            {
                var state = bySerial.TryGetValue(serial, out var status) ? StateOf(status) : TrayPhoneState.OutOfRange;
                return new TrayPhone(serial, displayName(serial), state, StatusText(state));
            })
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Serial, StringComparer.Ordinal)
            .ToList();

        return new TrayMenu(phones, Tooltip(phones));
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

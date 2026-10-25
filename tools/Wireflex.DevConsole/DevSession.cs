using Wireflex.Core.Configuration;
using Wireflex.Core.Decisions;
using Wireflex.Core.Monitoring;

namespace Wireflex.DevConsole;

/// <summary>
/// Liga os eventos do monitor ao motor e trata os comandos digitados. O motor não é thread-safe,
/// então tudo que chega nele passa pelo mesmo lock.
/// </summary>
internal sealed class DevSession(DecisionEngine engine, WireflexConfig config, ConsoleOutput output)
{
    public const string Help = """
        Comandos (N = número do celular em 'l'; pode omitir se houver só um):
          l | ls | 1   lista os celulares e o estado de cada um no motor (L minúsculo, "ls" ou o número 1)
          f [N]        simula: o usuário fechou o scrcpy
          x [N]        simula: o scrcpy encerrou sozinho (erro ou conexão caiu)
          s [N]        responde "sim" à pergunta de abrir o espelhamento
          n [N]        responde "não" à pergunta de abrir o espelhamento
          p [N]        pausa ou retoma o celular
          m <modo>     muda o modo global em memória: auto | perguntar | nunca
          r            reavalia todos os celulares
          h            mostra esta ajuda
          q            sai (o mesmo que Ctrl+C)
        """;

    private readonly object _sync = new();

    // Seriais na ordem do último 'l': o número digitado sempre aponta para o celular que o usuário viu.
    private List<string> _lastListed = [];

    public void OnDeviceEvent(DeviceEvent deviceEvent)
    {
        lock (_sync)
        {
            output.Event(Format.Event(deviceEvent));
            if (MonitorEventTranslator.Translate(deviceEvent) is { } input)
            {
                Apply(input);
            }
        }
    }

    /// <returns><c>false</c> quando o usuário pediu para sair.</returns>
    public bool Execute(string line)
    {
        // Entrada redirecionada pode chegar com BOM no começo.
        var parts = line.TrimStart('﻿')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var command = parts[0].ToLowerInvariant();
        var argument = parts.Length > 1 ? parts[1] : null;

        lock (_sync)
        {
            switch (command)
            {
                case "q" or "sair":
                    return false;
                case "h" or "ajuda" or "?":
                    output.Plain(Help);
                    break;
                // "l" se confunde com "1" na tela; os três listam.
                case "l" or "ls" or "1":
                    List();
                    break;
                case "r":
                    Apply(new Reevaluate(null, "pedido no DevConsole"));
                    break;
                case "m":
                    ChangeMode(argument);
                    break;
                case "f":
                    WithPhone(argument, phone => phone.ScrcpyAdbSerial is { } adb
                        ? new ScrcpyClosedByUser(phone.Serial, adb)
                        : NotMirroring(phone));
                    break;
                case "x":
                    WithPhone(argument, phone => phone.ScrcpyAdbSerial is { } adb
                        ? new ScrcpyExited(phone.Serial, adb)
                        : NotMirroring(phone));
                    break;
                case "s":
                    WithPhone(argument, phone => new UserAnswered(phone.Serial, true));
                    break;
                case "n":
                    WithPhone(argument, phone => new UserAnswered(phone.Serial, false));
                    break;
                case "p":
                    WithPhone(argument, phone => new PhonePauseChanged(phone.Serial, !phone.Paused));
                    break;
                default:
                    output.Warning($"Comando desconhecido: {command}. Digite h para ajuda.");
                    break;
            }
        }

        return true;
    }

    private void Apply(EngineInput input)
    {
        foreach (var action in engine.Handle(input))
        {
            if (action is DoNothing)
            {
                output.Idle(Format.Action(action));
            }
            else
            {
                output.Action(Format.Action(action) + " (simulado: nada é executado)");
            }
        }
    }

    private void List()
    {
        var phones = engine.GetStatus();
        _lastListed = phones.Select(p => p.Serial).ToList();
        if (phones.Count == 0)
        {
            output.Plain("  Nenhum celular visto ainda.");
            return;
        }

        output.Plain($"  Modo global: {Format.Mode(config.Global.ConnectionMode)}");
        for (var i = 0; i < phones.Count; i++)
        {
            output.Plain(Format.Status(i + 1, phones[i]));
        }
    }

    private void ChangeMode(string? argument)
    {
        if (argument is null || !CommandLineOptions.TryParseMode(argument, out var mode))
        {
            output.Warning("Use: m auto | m perguntar | m nunca");
            return;
        }

        config.Global.ConnectionMode = mode;
        output.Info($"Modo global agora: {Format.Mode(mode)} (só em memória)");
        Apply(new Reevaluate(null, "modo global mudou"));
    }

    private void WithPhone(string? argument, Func<PhoneStatus, EngineInput?> build)
    {
        var phones = engine.GetStatus();
        PhoneStatus? phone;

        if (argument is null)
        {
            // Sem número: vale se houver um único celular com alguma conexão.
            var present = phones.Where(p => p.Connections.Count > 0).ToList();
            if (present.Count != 1)
            {
                output.Warning(present.Count == 0
                    ? "Nenhum celular conectado agora."
                    : "Há mais de um celular: informe o número (veja 'l').");
                return;
            }

            phone = present[0];
        }
        else if (int.TryParse(argument, out var number) && number >= 1 && number <= _lastListed.Count)
        {
            var serial = _lastListed[number - 1];
            phone = phones.FirstOrDefault(p => p.Serial == serial);
            if (phone is null)
            {
                output.Warning($"O celular {number} não está mais no motor. Rode 'l' de novo.");
                return;
            }
        }
        else
        {
            output.Warning(_lastListed.Count == 0
                ? "Rode 'l' primeiro para ver os números."
                : $"Número inválido: {argument}. Veja 'l'.");
            return;
        }

        if (build(phone) is { } input)
        {
            Apply(input);
        }
    }

    private EngineInput? NotMirroring(PhoneStatus phone)
    {
        output.Warning($"{Format.Id(phone.Serial)} não está espelhando agora; nada a simular.");
        return null;
    }
}

using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Adb;
using Wireflex.Core.Logging;

namespace Wireflex.Core.Connection;

/// <summary>Como terminou a preparação do Wi-Fi.</summary>
public enum WifiPreparationOutcome
{
    /// <summary>Só aprendeu IP e nome (o Wi-Fi já estava conectado ou o modo não é tcpip).</summary>
    LearnedOnly,

    /// <summary>Modo TCP/IP ativo e conectado por Wi-Fi.</summary>
    Connected,

    /// <summary>O celular não está em nenhuma rede Wi-Fi.</summary>
    NoWifi,

    /// <summary>Não conseguiu conectar por Wi-Fi.</summary>
    ConnectFailed,

    /// <summary>O celular não respondeu pelo cabo (ex: adbd reiniciando); não dá para saber se há Wi-Fi.</summary>
    NotResponding,
}

/// <param name="Ip">IP do Wi-Fi do celular, se descobriu.</param>
/// <param name="DeviceName">Nome do aparelho (ex: "Galaxy de Fulano"), se descobriu.</param>
/// <param name="TcpipEnabled">Se rodou <c>tcpip</c> agora (o adbd do celular reiniciou).</param>
/// <param name="Detail">Mensagem do adb, quando houver (pode ter IP: mascare antes de mostrar).</param>
public sealed record WifiPreparation(string? Ip, string? DeviceName, WifiPreparationOutcome Outcome, bool TcpipEnabled, string? Detail = null);

/// <summary>O que se leu pelo cabo antes de mexer no Wi-Fi.</summary>
/// <param name="Responded">Algum comando de rede respondeu. <c>false</c>: não dá para saber se há Wi-Fi.</param>
/// <param name="TcpipActive">O modo TCP/IP já está ligado na porta certa (<c>service.adb.tcp.port</c>).</param>
public sealed record WifiProbe(string? Ip, string? DeviceName, bool Responded, bool TcpipActive);

public sealed class WifiPreparerOptions
{
    public int Port { get; init; } = WifiPreparer.DefaultTcpipPort;

    /// <summary>Espera depois do <c>tcpip</c> antes do primeiro <c>connect</c> (o adbd do celular reinicia).</summary>
    public TimeSpan TcpipSettleDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Limite de cada tentativa de <c>connect</c>. Com IP errado, o adb pode ficar preso bem mais que isso.</summary>
    public TimeSpan ConnectAttemptTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Limite de todas as tentativas de <c>connect</c> juntas.</summary>
    public TimeSpan ConnectLimit { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Espera entre uma tentativa recusada e a próxima.</summary>
    public TimeSpan ConnectRetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Prepara o Wi-Fi com o celular no cabo (Modo A), em passos que o coordenador chama na ordem:
/// <see cref="ProbeAsync"/> (IP, nome e se o TCP/IP já está ligado), <see cref="EnableTcpipAsync"/> (só se
/// precisar) e <see cref="ConnectAsync"/>. Só comandos pelo cabo e o <c>connect</c>.
/// </summary>
public sealed class WifiPreparer
{
    public const int DefaultTcpipPort = 5555;

    private readonly IAdbClient _adb;
    private readonly ILogger<WifiPreparer> _logger;
    private readonly TimeProvider _time;
    private readonly WifiPreparerOptions _options;

    public WifiPreparer(
        IAdbClient adb,
        ILogger<WifiPreparer>? logger = null,
        TimeProvider? timeProvider = null,
        WifiPreparerOptions? options = null)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _logger = logger ?? NullLogger<WifiPreparer>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new WifiPreparerOptions();
    }

    public int Port => _options.Port;

    /// <summary>Lê pelo cabo o IP do Wi-Fi, o nome do aparelho e a porta do TCP/IP. ~0,1 s no total.</summary>
    public async Task<WifiProbe> ProbeAsync(string usbAdbSerial, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(usbAdbSerial);

        var routes = await TryShellAsync(usbAdbSerial, "ip route", cancellationToken).ConfigureAwait(false);
        var ip = AndroidShellParser.ParseWifiIpFromRoutes(routes);
        var addresses = ip is null
            ? await TryShellAsync(usbAdbSerial, "ip -f inet addr show wlan0", cancellationToken).ConfigureAwait(false)
            : null;
        ip ??= AndroidShellParser.ParseInetAddress(addresses);
        var name = AndroidShellParser.ParseDeviceName(
            await TryShellAsync(usbAdbSerial, "settings get global device_name", cancellationToken).ConfigureAwait(false));
        var tcpPort = (await TryShellAsync(usbAdbSerial, "getprop service.adb.tcp.port", cancellationToken).ConfigureAwait(false))?.Trim();

        return new WifiProbe(
            ip,
            name,
            Responded: routes is not null || addresses is not null,
            TcpipActive: tcpPort == _options.Port.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Liga o modo TCP/IP. O adbd do celular reinicia e a conexão USB some e volta.</summary>
    /// <returns><c>false</c> se o celular recusou ou não respondeu.</returns>
    public async Task<bool> EnableTcpipAsync(string usbAdbSerial, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Ligando o modo TCP/IP (porta {Port}) em {Phone}", _options.Port, Sensitive.MaskAdbSerial(usbAdbSerial));
        var started = _time.GetTimestamp();
        try
        {
            await _adb.EnableTcpipAsync(usbAdbSerial, _options.Port, cancellationToken).ConfigureAwait(false);
            LogStep(usbAdbSerial, "tcpip", started, "ok");
            return true;
        }
        catch (Exception ex) when (ex is AdbException or IOException)
        {
            LogStep(usbAdbSerial, "tcpip", started, "falhou");
            _logger.LogWarning(ex, "Não foi possível ligar o modo TCP/IP");
            return false;
        }
    }

    /// <summary>
    /// Conecta por Wi-Fi: tenta de novo enquanto couber em <see cref="WifiPreparerOptions.ConnectLimit"/>, cada
    /// tentativa limitada a <see cref="WifiPreparerOptions.ConnectAttemptTimeout"/>.
    /// </summary>
    /// <param name="afterTcpip">O <c>tcpip</c> acabou de rodar: espera o adbd do celular reiniciar antes da 1ª tentativa.</param>
    public async Task<WifiPreparation> ConnectAsync(string usbAdbSerial, string ip, string? deviceName, bool afterTcpip, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        var address = Sensitive.Mask(ip) + ":" + _options.Port.ToString(CultureInfo.InvariantCulture);
        using var limit = new CancellationTokenSource(_options.ConnectLimit, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        string? detail = null;

        try
        {
            if (afterTcpip)
            {
                await Task.Delay(_options.TcpipSettleDelay, _time, linked.Token).ConfigureAwait(false);
            }

            for (var attempt = 1; ; attempt++)
            {
                if (attempt > 1)
                {
                    await Task.Delay(_options.ConnectRetryDelay, _time, linked.Token).ConfigureAwait(false);
                }

                var started = _time.GetTimestamp();
                using var attemptTimeout = new CancellationTokenSource(_options.ConnectAttemptTimeout, _time);
                using var attemptToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, attemptTimeout.Token);
                try
                {
                    var result = await _adb.ConnectAsync(ip, _options.Port, attemptToken.Token).ConfigureAwait(false);
                    detail = result.Message;
                    LogStep(usbAdbSerial, $"connect #{attempt}", started, result.Connected ? "conectou" : Sensitive.Scrub(detail));
                    if (result.Connected)
                    {
                        _logger.LogInformation("Wi-Fi conectado: {Address}", address);
                        return new WifiPreparation(ip, deviceName, WifiPreparationOutcome.Connected, afterTcpip, detail);
                    }
                }
                catch (OperationCanceledException) when (attemptTimeout.IsCancellationRequested && !linked.IsCancellationRequested)
                {
                    detail = $"connect não respondeu em {_options.ConnectAttemptTimeout.TotalSeconds:0} s";
                    LogStep(usbAdbSerial, $"connect #{attempt}", started, detail);
                }
                catch (Exception ex) when (ex is AdbException or IOException)
                {
                    detail = ex.Message;
                    LogStep(usbAdbSerial, $"connect #{attempt}", started, Sensitive.Scrub(detail));
                }
            }
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Wi-Fi de {Phone} não conectou em {Seconds:0} s",
                Sensitive.MaskAdbSerial(usbAdbSerial),
                _options.ConnectLimit.TotalSeconds);
            return new WifiPreparation(ip, deviceName, WifiPreparationOutcome.ConnectFailed, afterTcpip, detail ?? "tempo esgotado");
        }
    }

    /// <summary>Os três passos em sequência (sem soltar nada no meio). Usado em testes e ferramentas.</summary>
    /// <param name="connectWifi"><c>false</c>: só aprende IP e nome, sem <c>tcpip</c> nem <c>connect</c>.</param>
    /// <param name="onLearned">Chamado assim que IP e nome são lidos, antes do <c>tcpip</c> e do <c>connect</c>.</param>
    public async Task<WifiPreparation> PrepareAsync(
        string usbAdbSerial,
        bool connectWifi,
        CancellationToken cancellationToken,
        Action<string?, string?>? onLearned = null)
    {
        var probe = await ProbeAsync(usbAdbSerial, cancellationToken).ConfigureAwait(false);
        onLearned?.Invoke(probe.Ip, probe.DeviceName);

        if (!connectWifi)
        {
            return new WifiPreparation(probe.Ip, probe.DeviceName, WifiPreparationOutcome.LearnedOnly, TcpipEnabled: false);
        }

        if (NoWifiOutcome(usbAdbSerial, probe) is { } noWifi)
        {
            return noWifi;
        }

        var tcpipEnabled = false;
        if (!probe.TcpipActive)
        {
            if (!await EnableTcpipAsync(usbAdbSerial, cancellationToken).ConfigureAwait(false))
            {
                return new WifiPreparation(probe.Ip, probe.DeviceName, WifiPreparationOutcome.ConnectFailed, false, "tcpip falhou");
            }

            tcpipEnabled = true;
        }

        return await ConnectAsync(usbAdbSerial, probe.Ip!, probe.DeviceName, tcpipEnabled, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sem IP: "sem Wi-Fi" se o celular respondeu, "não respondeu" se não. <c>null</c> se há IP.</summary>
    public WifiPreparation? NoWifiOutcome(string usbAdbSerial, WifiProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.Ip is not null)
        {
            return null;
        }

        _logger.LogInformation(
            probe.Responded ? "{Phone} não está em nenhuma rede Wi-Fi" : "{Phone} não respondeu pelo cabo",
            Sensitive.MaskAdbSerial(usbAdbSerial));
        return new WifiPreparation(
            null,
            probe.DeviceName,
            probe.Responded ? WifiPreparationOutcome.NoWifi : WifiPreparationOutcome.NotResponding,
            TcpipEnabled: false);
    }

    private async Task<string?> TryShellAsync(string adbSerial, string command, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        try
        {
            var output = await _adb.ShellAsync(adbSerial, command, cancellationToken).ConfigureAwait(false);
            LogStep(adbSerial, command, started, "ok");
            return output;
        }
        catch (Exception ex) when (ex is AdbException or IOException)
        {
            LogStep(adbSerial, command, started, "falhou");
            _logger.LogDebug(ex, "Comando '{Command}' falhou em {Phone}", command, Sensitive.MaskAdbSerial(adbSerial));
            return null;
        }
    }

    /// <summary>Diagnóstico: quanto cada passo da preparação levou (relógio do <see cref="TimeProvider"/>, o mesmo do Stopwatch).</summary>
    private void LogStep(string adbSerial, string step, long startedTimestamp, string outcome) =>
        _logger.LogInformation(
            "Preparação {Phone}: {Step} em {Seconds:0.000} s ({Outcome})",
            Sensitive.MaskAdbSerial(adbSerial),
            step,
            _time.GetElapsedTime(startedTimestamp).TotalSeconds,
            outcome);
}

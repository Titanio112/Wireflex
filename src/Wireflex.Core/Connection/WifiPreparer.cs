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

/// <summary>
/// Prepara o Wi-Fi com o celular no cabo (Modo A): aprende o IP e o nome, liga o modo TCP/IP se ainda não
/// estiver ligado e conecta por Wi-Fi. Só comandos pelo cabo e o <c>connect</c>.
/// </summary>
public sealed class WifiPreparer
{
    public const int DefaultTcpipPort = 5555;

    private readonly IAdbClient _adb;
    private readonly ILogger<WifiPreparer> _logger;
    private readonly TimeProvider _time;
    private readonly int _port;
    private readonly int _connectAttempts;
    private readonly TimeSpan _connectRetryDelay;

    public WifiPreparer(
        IAdbClient adb,
        ILogger<WifiPreparer>? logger = null,
        TimeProvider? timeProvider = null,
        int port = DefaultTcpipPort,
        int connectAttempts = 5,
        TimeSpan? connectRetryDelay = null)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _logger = logger ?? NullLogger<WifiPreparer>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _port = port;
        _connectAttempts = Math.Max(1, connectAttempts);
        _connectRetryDelay = connectRetryDelay ?? TimeSpan.FromSeconds(1);
    }

    public int Port => _port;

    /// <param name="usbAdbSerial">Conexão USB do celular.</param>
    /// <param name="connectWifi"><c>false</c>: só aprende IP e nome, sem <c>tcpip</c> nem <c>connect</c>.</param>
    /// <param name="onLearned">Chamado assim que IP e nome são lidos, antes do <c>tcpip</c> e do <c>connect</c>:
    /// o que foi aprendido não se perde se o resto demorar ou falhar.</param>
    public async Task<WifiPreparation> PrepareAsync(
        string usbAdbSerial,
        bool connectWifi,
        CancellationToken cancellationToken,
        Action<string?, string?>? onLearned = null)
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
        onLearned?.Invoke(ip, name);

        // Nenhum dos dois comandos respondeu: não dá para saber se há Wi-Fi (ex: adbd reiniciando).
        var responded = routes is not null || addresses is not null;

        if (!connectWifi)
        {
            return new WifiPreparation(ip, name, WifiPreparationOutcome.LearnedOnly, TcpipEnabled: false);
        }

        if (ip is null)
        {
            _logger.LogInformation(
                responded ? "{Phone} não está em nenhuma rede Wi-Fi" : "{Phone} não respondeu pelo cabo",
                Sensitive.MaskAdbSerial(usbAdbSerial));
            return new WifiPreparation(
                null, name, responded ? WifiPreparationOutcome.NoWifi : WifiPreparationOutcome.NotResponding, TcpipEnabled: false);
        }

        var portText = _port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var currentPort = (await TryShellAsync(usbAdbSerial, "getprop service.adb.tcp.port", cancellationToken).ConfigureAwait(false))?.Trim();
        var tcpipEnabled = false;
        if (currentPort != portText)
        {
            _logger.LogInformation("Ligando o modo TCP/IP (porta {Port}) em {Phone}", _port, Sensitive.MaskAdbSerial(usbAdbSerial));
            var tcpipStarted = _time.GetTimestamp();
            try
            {
                await _adb.EnableTcpipAsync(usbAdbSerial, _port, cancellationToken).ConfigureAwait(false);
                tcpipEnabled = true;
                LogStep(usbAdbSerial, "tcpip", tcpipStarted, "ok");
            }
            catch (Exception ex) when (ex is AdbException or IOException)
            {
                LogStep(usbAdbSerial, "tcpip", tcpipStarted, "falhou");
                _logger.LogWarning(ex, "Não foi possível ligar o modo TCP/IP");
                return new WifiPreparation(ip, name, WifiPreparationOutcome.ConnectFailed, false, Sensitive.Scrub(ex.Message));
            }
        }

        string? detail = null;
        for (var attempt = 1; attempt <= _connectAttempts; attempt++)
        {
            if (tcpipEnabled || attempt > 1)
            {
                // Depois do tcpip o adbd do celular reinicia; dá um tempo antes de cada tentativa.
                await Task.Delay(_connectRetryDelay, _time, cancellationToken).ConfigureAwait(false);
            }

            var connectStarted = _time.GetTimestamp();
            try
            {
                var result = await _adb.ConnectAsync(ip, _port, cancellationToken).ConfigureAwait(false);
                detail = result.Message;
                LogStep(usbAdbSerial, $"connect #{attempt}", connectStarted, result.Connected ? "conectou" : Sensitive.Scrub(detail));
                if (result.Connected)
                {
                    _logger.LogInformation("Wi-Fi conectado: {Address}", Sensitive.Mask(ip) + ":" + portText);
                    return new WifiPreparation(ip, name, WifiPreparationOutcome.Connected, tcpipEnabled, detail);
                }
            }
            catch (Exception ex) when (ex is AdbException or IOException)
            {
                detail = ex.Message;
                LogStep(usbAdbSerial, $"connect #{attempt}", connectStarted, Sensitive.Scrub(detail));
            }
        }

        return new WifiPreparation(ip, name, WifiPreparationOutcome.ConnectFailed, tcpipEnabled, detail);
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

using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Adb;
using Wireflex.Core.Logging;

namespace Wireflex.Core.Monitoring;

/// <summary>
/// Acompanha o adb e publica eventos em <see cref="Events"/>: conexão apareceu, sumiu, mudou de estado,
/// celular mudou de transporte e adb disponível/indisponível.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Se o fluxo do adb terminar (servidor caiu ou reiniciou), tenta de novo com espera crescente.</item>
/// <item>Enquanto o adb está fora, mantém a última foto: ao voltar, só o que realmente mudou vira evento.</item>
/// <item>Mudanças só são publicadas depois de <see cref="DeviceMonitorOptions.Debounce"/> sem novidades.</item>
/// </list>
/// Uso único: chame <see cref="RunAsync"/> uma vez; ao terminar, <see cref="Events"/> é concluído.
/// </remarks>
public sealed class DeviceMonitor
{
    private readonly IAdbClient _adb;
    private readonly DeviceMonitorOptions _options;
    private readonly ILogger<DeviceMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly Channel<DeviceEvent> _events = Channel.CreateUnbounded<DeviceEvent>();
    private readonly Dictionary<string, string> _identityCache = new(StringComparer.Ordinal);
    private Dictionary<string, DeviceConnection> _current = new(StringComparer.Ordinal);
    private IReadOnlyList<AdbDevice>? _lastSnapshot;
    private int _started;

    public DeviceMonitor(
        IAdbClient adb,
        DeviceMonitorOptions? options = null,
        ILogger<DeviceMonitor>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _adb = adb ?? throw new ArgumentNullException(nameof(adb));
        _options = options ?? new DeviceMonitorOptions();
        _logger = logger ?? NullLogger<DeviceMonitor>.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    public ChannelReader<DeviceEvent> Events => _events.Reader;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("O monitor já foi iniciado.");
        }

        // Se o consumidor falhar, "stop" também para o produtor (e mata o track-devices).
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var items = Channel.CreateUnbounded<MonitorItem>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = ProduceAsync(items.Writer, stop.Token);

        try
        {
            await ConsumeAsync(items.Reader, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }

            _events.Writer.TryComplete();
        }
    }

    private async Task ProduceAsync(ChannelWriter<MonitorItem> writer, CancellationToken cancellationToken)
    {
        var delay = _options.InitialRetryDelay;
        var available = false;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string reason;
                try
                {
                    await foreach (var snapshot in _adb.TrackDevicesAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await writer.WriteAsync(new SnapshotItem(snapshot), cancellationToken).ConfigureAwait(false);
                        if (!available)
                        {
                            // Vai depois da foto: o consumidor aplica a foto e só então avisa que o adb está disponível.
                            available = true;
                            delay = _options.InitialRetryDelay;
                            _logger.LogInformation("Acompanhando o adb");
                            await writer.WriteAsync(new StatusItem(new MonitorStatusChanged(true, null)), cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }

                    reason = "o acompanhamento do adb terminou (servidor caiu ou reiniciou)";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    reason = Sensitive.Scrub(ex.Message);
                    _logger.LogWarning(ex, "Falha ao acompanhar o adb");
                }

                if (available)
                {
                    available = false;
                    await writer.WriteAsync(new StatusItem(new MonitorStatusChanged(false, reason)), cancellationToken)
                        .ConfigureAwait(false);

                    // Estava funcionando e caiu (ex: adb kill-server): a primeira nova tentativa é imediata.
                    // Só depois de falhar de novo entra a espera crescente.
                    _logger.LogInformation("adb indisponível ({Reason}); tentando de novo agora", reason);
                    continue;
                }

                _logger.LogInformation("adb indisponível ({Reason}); nova tentativa em {Delay} s", reason, delay.TotalSeconds);
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>
    /// Processa fotos e avisos na ordem em que chegam. Fotos esperam <see cref="DeviceMonitorOptions.Debounce"/>
    /// sem novidades; um aviso de status aplica antes a foto pendente, para o status sempre refletir a foto atual.
    /// Sem nada pendente, se houver conexão Wi-Fi sem identificação, tenta identificar de novo de tempos em tempos.
    /// </summary>
    private async Task ConsumeAsync(ChannelReader<MonitorItem> reader, CancellationToken cancellationToken)
    {
        IReadOnlyList<AdbDevice>? pending = null;

        // Só tenta identificar de novo com o adb no ar: com ele fora, "adb -s ... shell" subiria o servidor sozinho.
        var adbAvailable = false;

        while (true)
        {
            TimeSpan? wait = pending is not null ? _options.Debounce
                : adbAvailable && NeedsIdentityRetry ? _options.IdentityRetryDelay
                : null;

            if (wait is null)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            else
            {
                var outcome = await WaitForItemAsync(reader, wait.Value, cancellationToken).ConfigureAwait(false);
                if (outcome == WaitOutcome.TimedOut)
                {
                    if (pending is not null)
                    {
                        await ApplyAsync(pending, cancellationToken).ConfigureAwait(false);
                        pending = null;
                    }
                    else if (_lastSnapshot is not null)
                    {
                        _logger.LogDebug("Tentando de novo identificar conexões Wi-Fi");
                        await ApplyAsync(_lastSnapshot, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                if (outcome == WaitOutcome.Completed)
                {
                    break;
                }
            }

            while (reader.TryRead(out var item))
            {
                switch (item)
                {
                    case SnapshotItem snapshot:
                        pending = snapshot.Devices;
                        break;
                    case StatusItem status:
                        if (pending is not null)
                        {
                            await ApplyAsync(pending, cancellationToken).ConfigureAwait(false);
                            pending = null;
                        }

                        adbAvailable = status.Status.AdbAvailable;
                        _logger.LogInformation("{Event}", Describe(status.Status));
                        await _events.Writer.WriteAsync(status.Status, cancellationToken).ConfigureAwait(false);
                        break;
                }
            }
        }

        if (pending is not null && !cancellationToken.IsCancellationRequested)
        {
            await ApplyAsync(pending, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool NeedsIdentityRetry => _current.Values.Any(c =>
        c.PhoneSerial is null && c.Transport == AdbTransport.WifiTcpip && c.State == AdbDeviceState.Device);

    private async Task<WaitOutcome> WaitForItemAsync(ChannelReader<MonitorItem> reader, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var next = reader.WaitToReadAsync(waitSource.Token).AsTask();
        var timer = Task.Delay(timeout, _time, waitSource.Token);
        var first = await Task.WhenAny(next, timer).ConfigureAwait(false);
        await waitSource.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (first == timer)
        {
            return WaitOutcome.TimedOut;
        }

        return await next.ConfigureAwait(false) ? WaitOutcome.ItemReady : WaitOutcome.Completed;
    }

    private async Task ApplyAsync(IReadOnlyList<AdbDevice> snapshot, CancellationToken cancellationToken)
    {
        var next = new Dictionary<string, DeviceConnection>(StringComparer.Ordinal);
        foreach (var device in snapshot)
        {
            if (next.ContainsKey(device.Serial))
            {
                continue;
            }

            _current.TryGetValue(device.Serial, out var previous);
            var phoneSerial = await ResolvePhoneSerialAsync(device, previous, cancellationToken).ConfigureAwait(false);
            next[device.Serial] = new DeviceConnection(device.Serial, phoneSerial, device.Transport, device.State, device.DisplayModel);
        }

        var events = DeviceSnapshotDiff.Compute(_current, next);
        _current = next;
        _lastSnapshot = snapshot;

        foreach (var gone in _identityCache.Keys.Where(k => !next.ContainsKey(k)).ToList())
        {
            _identityCache.Remove(gone);
        }

        foreach (var e in events)
        {
            _logger.LogInformation("{Event}", Describe(e));
            await _events.Writer.WriteAsync(e, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> ResolvePhoneSerialAsync(AdbDevice device, DeviceConnection? previous, CancellationToken cancellationToken)
    {
        switch (device.Transport)
        {
            case AdbTransport.Usb:
            case AdbTransport.Emulator:
                return device.Serial == "(no serial number)" ? null : device.Serial;
        }

        if (_identityCache.TryGetValue(device.Serial, out var cached))
        {
            return cached;
        }

        if (previous?.PhoneSerial is { } known)
        {
            return known;
        }

        if (device.State == AdbDeviceState.Device)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.IdentityTimeout);
            try
            {
                var serial = await _adb.GetSerialNumberAsync(device.Serial, timeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(serial))
                {
                    _identityCache[device.Serial] = serial;
                    return serial;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug("Tempo esgotado ao identificar {Connection}", Sensitive.MaskAdbSerial(device.Serial));
            }
        }

        if (AdbSerialClassifier.TryExtractMdnsSerial(device.Serial, out var fromName))
        {
            return fromName;
        }

        return null;
    }

    private static string Describe(DeviceEvent e) => e switch
    {
        DeviceAppeared a => $"Apareceu {Mask(a.Connection)} via {a.Connection.Transport} ({a.Connection.State})",
        DeviceDisappeared d => $"Sumiu {Mask(d.Connection)} via {d.Connection.Transport}",
        DeviceStateChanged s => $"{Mask(s.Connection)} via {s.Connection.Transport}: {s.PreviousState} -> {s.Connection.State}",
        DeviceTransportChanged t => $"{Sensitive.MaskAdbSerial(t.PhoneKey)} mudou de {t.PreviousTransport} para {t.CurrentTransport}",
        MonitorStatusChanged m => $"adb disponível: {m.AdbAvailable}",
        _ => e.GetType().Name,
    };

    private static string Mask(DeviceConnection c) =>
        c.PhoneSerial is null ? Sensitive.MaskAdbSerial(c.AdbSerial) : Sensitive.Mask(c.PhoneSerial);

    /// <summary>Item da fila interna produtor -> consumidor.</summary>
    private abstract record MonitorItem;

    private sealed record SnapshotItem(IReadOnlyList<AdbDevice> Devices) : MonitorItem;

    private sealed record StatusItem(MonitorStatusChanged Status) : MonitorItem;

    private enum WaitOutcome
    {
        ItemReady,
        TimedOut,
        Completed,
    }
}

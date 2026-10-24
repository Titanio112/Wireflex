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

        var snapshots = Channel.CreateUnbounded<IReadOnlyList<AdbDevice>>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var producer = ProduceAsync(snapshots.Writer, cancellationToken);

        try
        {
            await ConsumeAsync(snapshots.Reader, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }

            _events.Writer.TryComplete();
        }
    }

    private async Task ProduceAsync(ChannelWriter<IReadOnlyList<AdbDevice>> writer, CancellationToken cancellationToken)
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
                        if (!available)
                        {
                            available = true;
                            delay = _options.InitialRetryDelay;
                            _logger.LogInformation("Acompanhando o adb");
                            await _events.Writer.WriteAsync(new MonitorStatusChanged(true, null), cancellationToken)
                                .ConfigureAwait(false);
                        }

                        await writer.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
                    }

                    reason = "o acompanhamento do adb terminou (servidor caiu ou reiniciou)";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    reason = Sensitive.ScrubIpAddresses(ex.Message);
                    _logger.LogWarning(ex, "Falha ao acompanhar o adb");
                }

                if (available)
                {
                    available = false;
                    await _events.Writer.WriteAsync(new MonitorStatusChanged(false, reason), cancellationToken)
                        .ConfigureAwait(false);
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

    private async Task ConsumeAsync(ChannelReader<IReadOnlyList<AdbDevice>> reader, CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            IReadOnlyList<AdbDevice>? latest = null;
            while (reader.TryRead(out var snapshot))
            {
                latest = snapshot;
            }

            // Espera um período sem novidades; cada nova foto reinicia a espera.
            var completed = false;
            while (!completed)
            {
                using var waitSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var next = reader.WaitToReadAsync(waitSource.Token).AsTask();
                var quiet = Task.Delay(_options.Debounce, _time, waitSource.Token);
                var first = await Task.WhenAny(next, quiet).ConfigureAwait(false);
                await waitSource.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (first == quiet)
                {
                    break;
                }

                if (!await next.ConfigureAwait(false))
                {
                    completed = true;
                }

                while (reader.TryRead(out var snapshot))
                {
                    latest = snapshot;
                }
            }

            if (latest is not null)
            {
                await ApplyAsync(latest, cancellationToken).ConfigureAwait(false);
            }
        }
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
        DeviceTransportChanged t => $"{Sensitive.Mask(t.PhoneKey)} mudou de {t.PreviousTransport} para {t.CurrentTransport}",
        MonitorStatusChanged m => $"adb disponível: {m.AdbAvailable}",
        _ => e.GetType().Name,
    };

    private static string Mask(DeviceConnection c) =>
        c.PhoneSerial is null ? Sensitive.MaskAdbSerial(c.AdbSerial) : Sensitive.Mask(c.PhoneSerial);
}

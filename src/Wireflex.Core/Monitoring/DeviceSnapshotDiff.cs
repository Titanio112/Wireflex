using Wireflex.Core.Adb;

namespace Wireflex.Core.Monitoring;

/// <summary>
/// Compara duas fotos das conexões e gera os eventos. Função pura.
/// </summary>
/// <remarks>
/// Ordem dos eventos: aparecimentos (cabo primeiro), mudanças de estado e só depois sumiços. Assim, quando o
/// cabo sai e o Wi-Fi já está lá, o motor vê primeiro que existe outro caminho e consegue trocar em vez de fechar.
/// </remarks>
public static class DeviceSnapshotDiff
{
    public static IReadOnlyList<DeviceEvent> Compute(
        IReadOnlyDictionary<string, DeviceConnection> previous,
        IReadOnlyDictionary<string, DeviceConnection> current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var appeared = new List<DeviceEvent>();
        var changed = new List<DeviceEvent>();
        var disappeared = new List<DeviceEvent>();

        foreach (var (adbSerial, now) in current.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!previous.TryGetValue(adbSerial, out var before))
            {
                appeared.Add(new DeviceAppeared(now));
            }
            else if (before.Key != now.Key)
            {
                // Descobrimos o serial real da conexão: para quem consome, é outro celular.
                disappeared.Add(new DeviceDisappeared(before));
                appeared.Add(new DeviceAppeared(now));
            }
            else if (before.State != now.State)
            {
                changed.Add(new DeviceStateChanged(now, before.State));
            }
        }

        foreach (var (adbSerial, before) in previous.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!current.ContainsKey(adbSerial))
            {
                disappeared.Add(new DeviceDisappeared(before));
            }
        }

        var events = new List<DeviceEvent>(appeared.Count + changed.Count + disappeared.Count);

        // Cabo antes do Wi-Fi: na partida, com os dois, o motor abre direto pelo cabo em vez de abrir pelo
        // Wi-Fi e trocar logo em seguida. OrderBy é estável: mantém a ordem do serial dentro do transporte.
        events.AddRange(appeared.OrderBy(e => AdbTransports.Priority(((DeviceAppeared)e).Connection.Transport)));
        events.AddRange(changed);
        events.AddRange(disappeared);
        events.AddRange(TransportChanges(previous.Values, current.Values));
        return events;
    }

    /// <summary>
    /// Melhor caminho até um celular: conexão utilizável primeiro, depois a prioridade do transporte (cabo antes).
    /// </summary>
    public static DeviceConnection? Primary(IEnumerable<DeviceConnection> connections) =>
        connections
            .OrderBy(c => c.State == AdbDeviceState.Device ? 0 : 1)
            .ThenBy(c => AdbTransports.Priority(c.Transport))
            .ThenBy(c => c.AdbSerial, StringComparer.Ordinal)
            .FirstOrDefault();

    private static IEnumerable<DeviceEvent> TransportChanges(
        IEnumerable<DeviceConnection> previous,
        IEnumerable<DeviceConnection> current)
    {
        var before = previous.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => Primary(g)!.Transport);
        var now = current.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => Primary(g)!.Transport);

        foreach (var (key, transport) in now.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (before.TryGetValue(key, out var old) && old != transport)
            {
                yield return new DeviceTransportChanged(key, old, transport);
            }
        }
    }
}

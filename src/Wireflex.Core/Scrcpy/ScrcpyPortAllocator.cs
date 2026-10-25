using System.Net.NetworkInformation;

namespace Wireflex.Core.Scrcpy;

/// <summary>
/// Escolhe a porta local (<c>--port</c>) de cada scrcpy do Wireflex.
/// </summary>
/// <remarks>
/// O scrcpy liga <c>SO_REUSEADDR</c> antes do <c>bind</c> (<c>app/src/util/net.c</c>); no Windows isso deixa
/// dois scrcpy abertos no mesmo instante escutarem na mesma porta padrão (27183) e trocarem de celular.
/// Por isso cada um ganha a sua porta, numa faixa fora da padrão do scrcpy (27183–27199), sem repetir entre
/// os abertos e pulando portas que já têm alguém escutando. A escolha gira pela faixa: uma porta recém-liberada
/// só volta depois das outras.
/// </remarks>
public sealed class ScrcpyPortAllocator
{
    public const int FirstPort = 27200;
    public const int LastPort = 27299;

    private readonly Func<IReadOnlySet<int>> _listeningPorts;
    private int _next = FirstPort;

    /// <param name="listeningPorts">Portas TCP com alguém escutando agora. Padrão: as da máquina.</param>
    public ScrcpyPortAllocator(Func<IReadOnlySet<int>>? listeningPorts = null)
    {
        _listeningPorts = listeningPorts ?? ReadListeningPorts;
    }

    /// <summary>A próxima porta livre, ou <c>null</c> se a faixa toda estiver ocupada (o scrcpy usa a padrão).</summary>
    /// <param name="taken">Portas dos scrcpy do Wireflex abertos agora.</param>
    public int? Next(IReadOnlyCollection<int> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var listening = _listeningPorts();
        const int count = LastPort - FirstPort + 1;
        for (var i = 0; i < count; i++)
        {
            var port = FirstPort + ((_next - FirstPort + i) % count);
            if (taken.Contains(port) || listening.Contains(port))
            {
                continue;
            }

            _next = port == LastPort ? FirstPort : port + 1;
            return port;
        }

        return null;
    }

    private static IReadOnlySet<int> ReadListeningPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return new HashSet<int>();
        }
    }
}

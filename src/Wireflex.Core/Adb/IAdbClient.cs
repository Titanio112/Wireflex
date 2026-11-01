namespace Wireflex.Core.Adb;

/// <summary>
/// Acesso ao adb. Toda a lógica usa esta interface para poder ser testada com uma versão falsa.
/// </summary>
public interface IAdbClient
{
    /// <summary>Lista os aparelhos uma vez (<c>host:devices-l</c>).</summary>
    Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Acompanha os aparelhos em tempo real (<c>host:track-devices-l</c>). Cada item é a lista completa
    /// do momento. Sobe o servidor do adb se ele não estiver rodando. O fluxo termina quando o servidor
    /// cai ou reinicia; quem consome decide se tenta de novo.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<AdbDevice>> TrackDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lê o serial real do celular (<c>getprop ro.serialno</c>). Usado para identificar conexões por Wi-Fi,
    /// cujo serial no adb é <c>IP:porta</c>. Só leitura. Retorna <c>null</c> se não conseguir.
    /// Não sobe o servidor do adb.
    /// </summary>
    Task<string?> GetSerialNumberAsync(string adbSerial, CancellationToken cancellationToken = default);

    /// <summary>Roda um comando no shell do celular e devolve a saída. Não sobe o servidor do adb.</summary>
    Task<string> ShellAsync(string adbSerial, string command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Liga o modo TCP/IP do adb no celular (<c>adb tcpip</c>). O adbd do celular reinicia e a conexão USB
    /// some e volta em seguida. Devolve a mensagem do celular.
    /// </summary>
    Task<string> EnableTcpipAsync(string adbSerial, int port, CancellationToken cancellationToken = default);

    /// <summary>Conecta por Wi-Fi (<c>adb connect</c>).</summary>
    Task<AdbConnectResult> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Desconecta uma conexão Wi-Fi (<c>adb disconnect</c>). <c>false</c> se o servidor não tinha essa conexão.
    /// Não sobe o servidor do adb.
    /// </summary>
    Task<bool> DisconnectAsync(string host, int port, CancellationToken cancellationToken = default);
}

/// <summary>Resultado de <c>adb connect</c>. A mensagem pode ter IP: mascare antes de mostrar.</summary>
public sealed record AdbConnectResult(bool Connected, string Message);

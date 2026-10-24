namespace Wireflex.Core.Adb;

/// <summary>
/// Acesso ao adb. Toda a lógica usa esta interface para poder ser testada com uma versão falsa.
/// </summary>
public interface IAdbClient
{
    /// <summary>Lista os aparelhos uma vez (<c>adb devices -l</c>).</summary>
    Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Acompanha os aparelhos em tempo real (<c>adb track-devices -l</c>). Cada item é a lista completa
    /// do momento. O fluxo termina quando o servidor do adb cai ou reinicia; quem consome decide se tenta de novo.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<AdbDevice>> TrackDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lê o serial real do celular (<c>getprop ro.serialno</c>). Usado para identificar conexões por Wi-Fi,
    /// cujo serial no adb é <c>IP:porta</c>. Só leitura. Retorna <c>null</c> se não conseguir.
    /// </summary>
    Task<string?> GetSerialNumberAsync(string adbSerial, CancellationToken cancellationToken = default);
}

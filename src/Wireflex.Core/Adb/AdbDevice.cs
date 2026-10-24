namespace Wireflex.Core.Adb;

/// <summary>
/// Uma linha de <c>adb devices -l</c> / <c>adb track-devices -l</c>.
/// </summary>
/// <param name="Serial">Serial do adb (identifica a conexão, não necessariamente o celular).</param>
/// <param name="State">Estado informado pelo adb.</param>
/// <param name="Transport">Transporte deduzido do serial.</param>
/// <param name="Model">Valor de <c>model:</c> como o adb envia (espaços viram <c>_</c>).</param>
/// <param name="Product">Valor de <c>product:</c>.</param>
/// <param name="DeviceName">Valor de <c>device:</c>.</param>
/// <param name="UsbPath">Caminho USB (ex: <c>usb:1-1</c>), quando o adb informa.</param>
/// <param name="TransportId">Valor de <c>transport_id:</c>.</param>
public sealed record AdbDevice(
    string Serial,
    AdbDeviceState State,
    AdbTransport Transport,
    string? Model = null,
    string? Product = null,
    string? DeviceName = null,
    string? UsbPath = null,
    long? TransportId = null)
{
    /// <summary>Modelo com espaços no lugar de <c>_</c> (ex: <c>SM M315F</c>).</summary>
    public string? DisplayModel => Model?.Replace('_', ' ');
}

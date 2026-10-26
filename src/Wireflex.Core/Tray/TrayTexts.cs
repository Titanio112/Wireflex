using Wireflex.Core.Adb;
using Wireflex.Core.Decisions;

namespace Wireflex.Core.Tray;

/// <summary>Textos das notificações e da pergunta da bandeja.</summary>
public static class TrayTexts
{
    public static string Question(string name, AdbTransport transport) =>
        $"{name} conectou {(transport == AdbTransport.Usb ? "pelo cabo" : "pelo Wi-Fi")}. Abrir o espelhamento?";

    public static string Notice(string name, DeviceNotice notice) => notice switch
    {
        DeviceNotice.Unauthorized => $"{name}: autorize a depuração USB na tela do celular.",
        DeviceNotice.Offline => $"{name}: o adb vê o celular, mas ele não responde (offline).",
        _ => $"{name}: {notice}",
    };

    public static string ScrcpyFailed(string name, string message) => $"O espelhamento de {name} não abriu: {message}";
}

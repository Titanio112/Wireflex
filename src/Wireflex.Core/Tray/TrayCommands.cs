using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Decisions;

namespace Wireflex.Core.Tray;

/// <summary>
/// O que cada item do menu da bandeja faz. Só fala com o coordenador e a config; a bandeja só chama.
/// Valem também para celular que só apareceu pelo Wi-Fi.
/// </summary>
public sealed class TrayCommands(ConnectionCoordinator coordinator, PhoneCatalog catalog)
{
    /// <summary>Abre mesmo se o usuário tinha fechado a janela ou se o modo for perguntar.</summary>
    public void OpenNow(string serial) => coordinator.Post(new OpenRequested(serial));

    /// <summary>Pausa (fecha o scrcpy e não abre) ou retoma. Só até fechar o Wireflex.</summary>
    public void SetPaused(string serial, bool paused) => coordinator.Post(new PhonePauseChanged(serial, paused));

    /// <summary>
    /// Desativar = modo nunca só para este celular (fecha o scrcpy aberto), gravado na config; cria a entrada se ele
    /// ainda não estiver lá. Ativar volta a seguir o modo global.
    /// </summary>
    public async Task SetEnabledAsync(string serial, bool enabled)
    {
        await catalog.SetPhoneConnectionModeAsync(serial, enabled ? null : ConnectionMode.Never).ConfigureAwait(false);
        coordinator.Post(new Reevaluate(serial, enabled ? "celular ativado" : "celular desativado"));
    }

    /// <summary>Tira só este celular da config e desconecta o Wi-Fi dele. Se estiver ligado, fica ignorado até sair do alcance.</summary>
    public Task ForgetAsync(string serial) => coordinator.ForgetAsync(serial);
}

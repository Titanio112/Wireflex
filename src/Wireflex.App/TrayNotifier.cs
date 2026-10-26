using Wireflex.Core.Decisions;
using Wireflex.Core.Execution;

namespace Wireflex.App;

/// <summary>Perguntas e avisos do executor, mostrados na bandeja. Chamado de qualquer thread.</summary>
internal sealed class TrayNotifier(Func<TrayIcon?> tray, Func<string, string> displayName) : IUserNotifier
{
    public void Ask(AskUser request)
    {
        // A pergunta com Sim/Não entra junto com o diálogo; por enquanto, só o aviso.
        tray()?.ShowNotification(Core.AppInfo.Name, $"{displayName(request.Serial!)} conectou (modo perguntar).");
    }

    public void Notify(NotifyUser notice) =>
        tray()?.ShowNotification(Core.AppInfo.Name, $"{displayName(notice.Serial!)}: {notice.Notice}", warning: true);

    public void ScrcpyFailed(string serial, string message) =>
        tray()?.ShowNotification(Core.AppInfo.Name, $"{displayName(serial)}: {message}", warning: true);
}

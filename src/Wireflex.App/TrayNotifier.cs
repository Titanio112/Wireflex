using System.Windows.Threading;
using Wireflex.Core;
using Wireflex.Core.Decisions;
using Wireflex.Core.Execution;
using Wireflex.Core.Tray;

namespace Wireflex.App;

/// <summary>
/// Perguntas e avisos do executor: a pergunta vai para o diálogo (<see cref="AskController"/>), os avisos viram
/// notificação da bandeja. Chamado de qualquer thread.
/// </summary>
internal sealed class TrayNotifier(
    Dispatcher dispatcher,
    Func<TrayIcon?> tray,
    Func<AskController?> questions,
    Func<string, string> displayName) : IUserNotifier
{
    public void Ask(AskUser request) =>
        dispatcher.BeginInvoke(() => questions()?.Ask(request.Serial!, request.Transport));

    public void Notify(NotifyUser notice) =>
        tray()?.ShowNotification(AppInfo.Name, TrayTexts.Notice(displayName(notice.Serial!), notice.Notice), warning: true);

    public void ScrcpyFailed(string serial, string message) =>
        tray()?.ShowNotification(AppInfo.Name, TrayTexts.ScrcpyFailed(displayName(serial), message), warning: true);
}

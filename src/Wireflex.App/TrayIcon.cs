using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Wireflex.App;

/// <summary>Ícone na bandeja (NotifyIcon do WinForms). Tudo aqui roda na thread da interface.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Dispatcher _dispatcher;

    public TrayIcon(Dispatcher dispatcher, System.Drawing.Icon icon, Action exit)
    {
        _dispatcher = dispatcher;
        _menu.Items.Add("Sair", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = Core.AppInfo.Name,
            ContextMenuStrip = _menu,
            Visible = true,
        };
    }

    /// <summary>Notificação da bandeja (no Windows 10/11 aparece como notificação do sistema). Pode ser chamado de qualquer thread.</summary>
    public void ShowNotification(string title, string text, bool warning = false) =>
        _dispatcher.BeginInvoke(() => _icon.ShowBalloonTip(
            5000,
            title,
            text,
            warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info));

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}

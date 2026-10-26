using System.Windows.Threading;
using Wireflex.Core;
using Wireflex.Core.Tray;
using Forms = System.Windows.Forms;

namespace Wireflex.App;

/// <summary>
/// Ícone na bandeja (NotifyIcon do WinForms). Só desenha: o conteúdo vem do <see cref="TrayMenuModel"/>.
/// Tudo aqui roda na thread da interface; <see cref="RequestRefresh"/> e <see cref="ShowNotification"/> podem ser
/// chamados de qualquer thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Dispatcher _dispatcher;
    private readonly Action _exit;
    private int _refreshPending;

    public TrayIcon(Dispatcher dispatcher, System.Drawing.Icon icon, Action exit)
    {
        _dispatcher = dispatcher;
        _exit = exit;
        _menu.Opening += (_, _) => Rebuild();
        Rebuild();
        _icon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = AppInfo.Name,
            ContextMenuStrip = _menu,
            Visible = true,
        };
    }

    /// <summary>De onde vem o conteúdo do menu. <c>null</c> enquanto o Wireflex não está acompanhando os celulares.</summary>
    public Func<TrayMenu>? MenuSource { get; set; }

    /// <summary>Texto mostrado no lugar da lista quando <see cref="MenuSource"/> é <c>null</c>.</summary>
    public string StatusMessage { get; set; } = "Iniciando...";

    /// <summary>O ícone da bandeja do Wireflex (embutido no executável).</summary>
    public static System.Drawing.Icon LoadIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Wireflex.App.wireflex.ico")
            ?? throw new InvalidOperationException("Ícone não encontrado nos recursos.");
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    /// <summary>Atualiza o tooltip (agrupa vários pedidos seguidos num só).</summary>
    public void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Volatile.Write(ref _refreshPending, 0);
                _icon.Text = MenuSource?.Invoke().Tooltip ?? $"{AppInfo.Name}: {StatusMessage}";
            });
        }
    }

    /// <summary>Notificação da bandeja (no Windows 10/11 aparece como notificação do sistema).</summary>
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

    private void Rebuild()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new Forms.ToolStripMenuItem(AppInfo.Name) { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());

        if (MenuSource?.Invoke() is not { } menu)
        {
            _menu.Items.Add(new Forms.ToolStripMenuItem(Escape(StatusMessage)) { Enabled = false });
        }
        else if (menu.Phones.Count == 0)
        {
            _menu.Items.Add(new Forms.ToolStripMenuItem("Nenhum celular conhecido. Plugue o cabo para começar.") { Enabled = false });
        }
        else
        {
            foreach (var phone in menu.Phones)
            {
                _menu.Items.Add(new Forms.ToolStripMenuItem(Escape($"{phone.Name}: {phone.StatusText}")));
            }
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Sair", null, (_, _) => _exit());
    }

    /// <summary>No menu do WinForms, &amp; marca a tecla de atalho: o nome do celular pode ter.</summary>
    private static string Escape(string text) => text.Replace("&", "&&", StringComparison.Ordinal);
}

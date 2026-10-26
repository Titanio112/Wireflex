using System.Windows.Threading;
using Wireflex.Core;
using Wireflex.Core.Configuration;
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

    /// <summary>O que os itens de cada celular fazem (junto com <see cref="MenuSource"/>).</summary>
    public TrayCommands? Commands { get; set; }

    /// <summary>Depois de cada atualização do estado (na thread da interface).</summary>
    public event Action? Refreshed;

    /// <summary>Falha de um comando do menu (para o log).</summary>
    public Action<Exception>? OnError { get; set; }

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
                Refreshed?.Invoke();
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

        var menu = MenuSource?.Invoke();
        if (menu is null)
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
                _menu.Items.Add(PhoneItem(phone));
            }
        }

        if (menu is not null && Commands is not null)
        {
            _menu.Items.Add(new Forms.ToolStripSeparator());
            _menu.Items.Add(ModeItem(menu.GlobalMode));
        }

        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Sair", null, (_, _) => _exit());
    }

    private Forms.ToolStripMenuItem PhoneItem(TrayPhone phone)
    {
        var serial = phone.Serial;
        var item = new Forms.ToolStripMenuItem(Escape($"{phone.Name}: {phone.StatusText}"));
        item.DropDownItems.Add(new Forms.ToolStripMenuItem("Abrir agora", null, (_, _) => Run(c => c.OpenNow(serial)))
        {
            Enabled = phone.CanOpenNow,
        });
        if (phone.State == TrayPhoneState.WaitingForAnswer)
        {
            // Responde a pergunta "abrir?" pelo menu (o "Abrir agora" acima é o sim).
            item.DropDownItems.Add(new Forms.ToolStripMenuItem("Não abrir", null, (_, _) => Run(c => c.Answer(serial, false))));
        }
        item.DropDownItems.Add(new Forms.ToolStripMenuItem(phone.Paused ? "Retomar" : "Pausar", null, (_, _) => Run(c => c.SetPaused(serial, !phone.Paused)))
        {
            Enabled = phone.CanPause,
            ToolTipText = "Só até fechar o Wireflex",
        });
        item.DropDownItems.Add(new Forms.ToolStripMenuItem("Desativado", null, (_, _) => RunAsync(c => c.SetEnabledAsync(serial, phone.Disabled)))
        {
            Checked = phone.Disabled,
            Enabled = phone.CanChangeSettings,
            ToolTipText = "Não abre o espelhamento deste celular (fica gravado)",
        });
        item.DropDownItems.Add(new Forms.ToolStripSeparator());
        item.DropDownItems.Add(new Forms.ToolStripMenuItem("Esquecer...", null, (_, _) => Forget(phone))
        {
            Enabled = phone.CanChangeSettings,
        });
        return item;
    }

    private Forms.ToolStripMenuItem ModeItem(ConnectionMode current)
    {
        var item = new Forms.ToolStripMenuItem("Modo de conexão");
        foreach (var mode in (ConnectionMode[])[ConnectionMode.Automatic, ConnectionMode.Ask, ConnectionMode.Never])
        {
            item.DropDownItems.Add(new Forms.ToolStripMenuItem(TrayMenuModel.ModeText(mode), null, (_, _) => RunAsync(c => c.SetGlobalModeAsync(mode)))
            {
                Checked = mode == current,
            });
        }

        return item;
    }

    private void Forget(TrayPhone phone)
    {
        var answer = System.Windows.MessageBox.Show(
            $"Esquecer {phone.Name}?\n\nEle sai da lista e o Wi-Fi dele é desconectado. Se estiver ligado agora, "
                + "fica ignorado até sair do alcance (sem cabo e sem Wi-Fi) e voltar.",
            AppInfo.Name,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No);
        if (answer == System.Windows.MessageBoxResult.Yes)
        {
            RunAsync(c => c.ForgetAsync(phone.Serial));
        }
    }

    private void Run(Action<TrayCommands> command) => RunAsync(c =>
    {
        command(c);
        return Task.CompletedTask;
    });

    private async void RunAsync(Func<TrayCommands, Task> command)
    {
        if (Commands is not { } commands)
        {
            return;
        }

        try
        {
            await command(commands);
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }

        RequestRefresh();
    }

    /// <summary>No menu do WinForms, &amp; marca a tecla de atalho: o nome do celular pode ter.</summary>
    private static string Escape(string text) => text.Replace("&", "&&", StringComparison.Ordinal);
}

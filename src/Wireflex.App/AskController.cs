using Wireflex.Core.Adb;
using Wireflex.Core.Tray;

namespace Wireflex.App;

/// <summary>
/// Mostra as perguntas da fila (<see cref="QuestionQueue"/>) uma de cada vez. Roda na thread da interface.
/// A resposta pode vir do diálogo ou do menu; quem deixou de esperar (abriu, saiu do alcance) sai da fila sozinho.
/// </summary>
internal sealed class AskController(
    Func<TrayCommands?> commands,
    Func<string, string> displayName,
    Func<string, bool> stillWaiting)
{
    private readonly QuestionQueue _queue = new();
    private readonly Dictionary<string, AdbTransport> _transports = new(StringComparer.Ordinal);
    private AskWindow? _window;
    private string? _windowSerial;

    public void Ask(string serial, AdbTransport transport)
    {
        _transports[serial] = transport;
        if (_queue.Enqueue(serial))
        {
            ShowCurrent();
        }
    }

    /// <summary>O estado mudou: tira as perguntas que não esperam mais resposta.</summary>
    public void Refresh()
    {
        if (_queue.Prune(stillWaiting))
        {
            CloseWindow();
            ShowCurrent();
        }
    }

    private void ShowCurrent()
    {
        if (_window is not null || _queue.Current is not { } serial)
        {
            return;
        }

        var transport = _transports.GetValueOrDefault(serial, AdbTransport.Usb);
        var window = new AskWindow(TrayTexts.Question(displayName(serial), transport));
        window.Answered += answer => OnAnswered(serial, answer);
        _window = window;
        _windowSerial = serial;
        window.Show();
    }

    private void OnAnswered(string serial, bool? answer)
    {
        if (_windowSerial == serial)
        {
            _window = null;
            _windowSerial = null;
        }

        if (answer is { } accepted)
        {
            commands()?.Answer(serial, accepted);
        }

        // Fechou sem responder: a pergunta continua no menu, mas o diálogo não volta.
        _queue.Remove(serial);
        _transports.Remove(serial);
        ShowCurrent();
    }

    private void CloseWindow()
    {
        var window = _window;
        _window = null;
        _windowSerial = null;
        window?.Close();
    }
}

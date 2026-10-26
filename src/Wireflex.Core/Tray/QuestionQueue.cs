namespace Wireflex.Core.Tray;

/// <summary>
/// Perguntas "abrir o espelhamento?" esperando resposta, uma por celular, mostradas uma de cada vez.
/// Sem tela: o diálogo só mostra <see cref="Current"/>. Não é thread-safe (fica na thread da interface).
/// </summary>
public sealed class QuestionQueue
{
    private readonly List<string> _pending = [];

    /// <summary>Celular da pergunta na tela agora (<c>null</c> = nenhuma).</summary>
    public string? Current => _pending.Count > 0 ? _pending[0] : null;

    public IReadOnlyList<string> Pending => _pending;

    /// <summary>Entra na fila (uma vez por celular). <c>true</c> se virou a pergunta da vez (mostrar agora).</summary>
    public bool Enqueue(string serial)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        if (_pending.Contains(serial, StringComparer.Ordinal))
        {
            return false;
        }

        _pending.Add(serial);
        return _pending.Count == 1;
    }

    /// <summary>Respondida (pelo diálogo ou pelo menu): sai da fila. Devolve a próxima a mostrar, se a da vez mudou.</summary>
    public string? Remove(string serial)
    {
        var wasCurrent = Current == serial;
        _pending.Remove(serial);
        return wasCurrent ? Current : null;
    }

    /// <summary>
    /// Tira as que não esperam mais resposta (o celular saiu do alcance, abriu pelo "Abrir agora", o modo mudou).
    /// Devolve <c>true</c> se a pergunta da vez mudou (fechar o diálogo e mostrar a próxima, se houver).
    /// </summary>
    public bool Prune(Func<string, bool> stillWaiting)
    {
        ArgumentNullException.ThrowIfNull(stillWaiting);
        var before = Current;
        _pending.RemoveAll(serial => !stillWaiting(serial));
        return before != Current;
    }
}

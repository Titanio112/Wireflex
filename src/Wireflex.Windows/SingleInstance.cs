namespace Wireflex.Windows;

/// <summary>
/// Garante uma instância por sessão do Windows: um mutex com nome (existe enquanto a primeira instância tiver o
/// handle aberto) e um evento com nome que a segunda instância sinaliza antes de fechar.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => ActivationRequested?.Invoke(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Outra execução pediu para abrir. Chamado numa thread do pool.</summary>
    public event Action? ActivationRequested;

    /// <summary>Vira a instância única, ou devolve <c>null</c> se já houver uma rodando nesta sessão.</summary>
    /// <param name="name">Nome sem prefixo (ex: <c>Wireflex</c>); fica em <c>Local\</c>, só nesta sessão.</param>
    public static SingleInstance? TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(false, MutexName(name), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, EventName(name)));
    }

    /// <summary>Avisa a instância que já está rodando. <c>false</c> se não houver nenhuma.</summary>
    public static bool SignalExisting(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!EventWaitHandle.TryOpenExisting(EventName(name), out var existing))
        {
            return false;
        }

        using (existing)
        {
            return existing.Set();
        }
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _activate.Dispose();
        _mutex.Dispose();
    }

    private static string MutexName(string name) => $@"Local\{name}.Instance";

    private static string EventName(string name) => $@"Local\{name}.Activate";
}

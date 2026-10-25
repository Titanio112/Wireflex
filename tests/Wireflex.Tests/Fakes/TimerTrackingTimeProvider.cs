using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Wireflex.Tests.Fakes;

/// <summary>
/// Relógio falso que avisa quando um timer é criado. O teste espera o timer com o atraso esperado
/// (ex: 2 s de espera crescente) e só então avança o tempo: nada de espera real nem tolerância.
/// </summary>
internal sealed class TimerTrackingTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _created.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>Espera até ser criado um timer com este atraso (ignora os outros) e avança o relógio por ele.</summary>
    public async Task AdvanceWhenTimerCreatedAsync(TimeSpan dueTime)
    {
        await WaitForTimerAsync(dueTime);
        Advance(dueTime);
    }

    public async Task WaitForTimerAsync(TimeSpan dueTime)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Real);
        while (await _created.Reader.ReadAsync(timeout.Token) != dueTime)
        {
        }
    }
}

internal static class TestTimeouts
{
    /// <summary>Limite real só para o teste não travar se algo der errado; o caminho feliz não espera.</summary>
    public static readonly TimeSpan Real = TimeSpan.FromSeconds(10);
}

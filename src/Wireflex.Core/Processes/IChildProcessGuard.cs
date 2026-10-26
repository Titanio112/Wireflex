using System.Diagnostics;

namespace Wireflex.Core.Processes;

/// <summary>
/// Amarra processos filhos ao Wireflex: se o Wireflex morrer (até à força), eles morrem junto.
/// A versão do Windows (Job Object) fica em <c>Wireflex.Windows</c>; o Core não referencia o Windows.
/// </summary>
public interface IChildProcessGuard
{
    void Track(Process process);
}

/// <summary>Não amarra nada. Para testes e plataformas sem suporte.</summary>
public sealed class NoChildProcessGuard : IChildProcessGuard
{
    public static NoChildProcessGuard Instance { get; } = new();

    public void Track(Process process)
    {
    }
}

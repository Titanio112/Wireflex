namespace Wireflex.Core.Scrcpy;

/// <summary>Abre o scrcpy. Os testes usam uma versão falsa.</summary>
public interface IScrcpyLauncher
{
    /// <exception cref="ScrcpyException">Não foi possível iniciar o processo.</exception>
    IScrcpyProcess Start(ScrcpyLaunchRequest request);
}

/// <param name="PhoneSerial">Serial do celular (só para mascarar a saída do scrcpy no log).</param>
/// <param name="AdbSerial">Conexão a usar (<c>-s</c>): serial USB ou <c>IP:porta</c>.</param>
/// <param name="WindowTitle">Título da janela.</param>
/// <param name="WindowTitleForLog">Título para o log, sem dados pessoais (ex: o nome do aparelho trocado por um marcador).</param>
public sealed record ScrcpyLaunchRequest(string PhoneSerial, string AdbSerial, string WindowTitle, string? WindowTitleForLog = null)
{
    public string SafeWindowTitle => WindowTitleForLog ?? WindowTitle;
}

/// <summary>Um scrcpy rodando.</summary>
public interface IScrcpyProcess
{
    int Id { get; }

    /// <summary>Completa com o código de saída quando o processo termina.</summary>
    Task<int> Exited { get; }

    /// <summary>Título atual da janela principal do processo (vazio se ainda não abriu ou já fechou).</summary>
    string ReadWindowTitle();

    /// <summary>Encerra o scrcpy (e os adb que ele abriu).</summary>
    void Kill();
}

/// <summary>Como o scrcpy terminou, pelo código de saída.</summary>
public enum ScrcpyExitKind
{
    /// <summary>0: encerramento normal, ou seja, o usuário fechou a janela.</summary>
    ClosedByUser,

    /// <summary>1 logo depois de abrir (até <see cref="ScrcpyExit.StartupWindow"/>): falhou ao iniciar.</summary>
    StartFailed,

    /// <summary>2, ou 1 depois de rodar mais que <see cref="ScrcpyExit.StartupWindow"/>: o celular desconectou com o scrcpy aberto.</summary>
    DeviceDisconnected,

    /// <summary>Qualquer outro código (ex: processo morto).</summary>
    Other,
}

public static class ScrcpyExit
{
    /// <summary>
    /// Até quanto tempo depois de abrir um código 1 conta como falha ao iniciar. Depois disso, o 1 é o
    /// celular sumindo (visto no teste: o scrcpy rodou 37 s e saiu com 1 quando o cabo saiu).
    /// </summary>
    public static readonly TimeSpan StartupWindow = TimeSpan.FromSeconds(10);

    /// <summary>Códigos documentados em <c>scrcpy --help</c> ("Exit status"), sem saber quanto tempo rodou.</summary>
    public static ScrcpyExitKind Classify(int exitCode) => Classify(exitCode, TimeSpan.Zero);

    /// <param name="runtime">Quanto tempo o scrcpy ficou aberto.</param>
    public static ScrcpyExitKind Classify(int exitCode, TimeSpan runtime) => exitCode switch
    {
        0 => ScrcpyExitKind.ClosedByUser,
        1 when runtime > StartupWindow => ScrcpyExitKind.DeviceDisconnected,
        1 => ScrcpyExitKind.StartFailed,
        2 => ScrcpyExitKind.DeviceDisconnected,
        _ => ScrcpyExitKind.Other,
    };
}

public sealed class ScrcpyException : Exception
{
    public ScrcpyException(string message)
        : base(message)
    {
    }

    public ScrcpyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

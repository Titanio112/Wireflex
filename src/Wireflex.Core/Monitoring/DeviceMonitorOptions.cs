namespace Wireflex.Core.Monitoring;

public sealed class DeviceMonitorOptions
{
    /// <summary>
    /// Tempo de silêncio exigido antes de publicar mudanças. Evita eventos "piscando" quando o adb oscila
    /// (ex: offline por um instante ao plugar o cabo).
    /// </summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>Primeira espera antes de reconectar ao adb. Dobra a cada falha seguida.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Espera máxima entre tentativas de reconectar ao adb.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tempo máximo para descobrir o serial real de uma conexão por Wi-Fi.</summary>
    public TimeSpan IdentityTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Intervalo para tentar de novo identificar uma conexão Wi-Fi pronta cujo serial ainda não se sabe.</summary>
    public TimeSpan IdentityRetryDelay { get; init; } = TimeSpan.FromSeconds(10);
}

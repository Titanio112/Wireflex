namespace Wireflex.Core.Configuration;

public interface IConfigStore
{
    /// <summary>
    /// Lê a configuração. Nunca falha por arquivo ausente ou corrompido: nesses casos devolve o padrão
    /// (e, se corrompido, guarda uma cópia <c>.bak</c>).
    /// </summary>
    Task<WireflexConfig> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Grava de forma segura: arquivo temporário e depois troca.</summary>
    Task SaveAsync(WireflexConfig config, CancellationToken cancellationToken = default);
}

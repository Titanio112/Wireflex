using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wireflex.Core.Configuration;

/// <summary>
/// Guarda a configuração em <c>config.json</c> (por padrão em <c>%APPDATA%\Wireflex</c>).
/// </summary>
public sealed class JsonConfigStore : IConfigStore
{
    public const string FileName = "config.json";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<JsonConfigStore> _logger;

    public JsonConfigStore(string directory, ILogger<JsonConfigStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        FilePath = Path.Combine(directory, FileName);
        _logger = logger ?? NullLogger<JsonConfigStore>.Instance;
    }

    public string Directory { get; }

    public string FilePath { get; }

    /// <summary>Cópia do último arquivo corrompido ou de versão não suportada.</summary>
    public string BackupPath => FilePath + ".bak";

    public async Task<WireflexConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                _logger.LogInformation("Configuração não encontrada; criando a padrão");
                var created = new WireflexConfig();
                await WriteAsync(created, cancellationToken).ConfigureAwait(false);
                return created;
            }

            string text;
            try
            {
                text = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Arquivo travado ou sem permissão: segue com o padrão em memória, sem sobrescrever nada.
                _logger.LogError(ex, "Não foi possível ler a configuração; usando a padrão sem gravar");
                return new WireflexConfig();
            }

            try
            {
                var config = Parse(text, out var fromVersion);

                if (fromVersion < WireflexConfig.CurrentSchemaVersion)
                {
                    _logger.LogInformation("Configuração migrada da versão {From} para {To}", fromVersion, WireflexConfig.CurrentSchemaVersion);
                    File.Copy(FilePath, $"{FilePath}.v{fromVersion}.bak", overwrite: true);
                    await WriteAsync(config, cancellationToken).ConfigureAwait(false);
                }

                return config;
            }
            catch (Exception ex) when (ex is JsonException or UnsupportedConfigVersionException)
            {
                _logger.LogWarning(ex, "Configuração inválida; cópia guardada em {Backup} e padrão recriada", Path.GetFileName(BackupPath));
                File.Copy(FilePath, BackupPath, overwrite: true);
                var fallback = new WireflexConfig();
                await WriteAsync(fallback, cancellationToken).ConfigureAwait(false);
                return fallback;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Lê a configuração <b>sem gravar nada</b>: não cria o arquivo, não migra no disco, não faz <c>.bak</c>.
    /// Retorna <c>null</c> se o arquivo não existir, não puder ser lido ou for inválido.
    /// Para ferramentas que não podem mexer na configuração do usuário (ex: DevConsole).
    /// </summary>
    public async Task<WireflexConfig?> TryReadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var text = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
            return Parse(text, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UnsupportedConfigVersionException)
        {
            _logger.LogWarning(ex, "Não foi possível ler a configuração (somente leitura)");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(WireflexConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAsync(config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WriteAsync(WireflexConfig config, CancellationToken cancellationToken)
    {
        config.SchemaVersion = WireflexConfig.CurrentSchemaVersion;
        Normalize(config);
        System.IO.Directory.CreateDirectory(Directory);

        var temporary = FilePath + ".tmp";
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, config, ConfigJsonContext.Default.WireflexConfig, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Troca atômica no mesmo volume: quem lê vê o arquivo antigo inteiro ou o novo inteiro.
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>Interpreta e migra em memória. Lança <see cref="JsonException"/> ou <see cref="UnsupportedConfigVersionException"/>.</summary>
    private static WireflexConfig Parse(string text, out int fromVersion)
    {
        try
        {
            var root = JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject
                ?? throw new JsonException("A raiz da configuração não é um objeto.");
            fromVersion = ConfigMigrator.Migrate(root);
            var config = JsonSerializer.Deserialize(root, ConfigJsonContext.Default.WireflexConfig)
                ?? throw new JsonException("Configuração vazia.");
            Normalize(config);
            return config;
        }
        catch (ArgumentException ex)
        {
            // JsonObject lança ArgumentException quando a mesma propriedade aparece duas vezes.
            throw new JsonException("Propriedade repetida na configuração.", ex);
        }
    }

    private static void Normalize(WireflexConfig config)
    {
        config.Global ??= new GlobalSettings();
        config.Phones ??= [];

        // O conversor de enum aceita números (ex: "connectionMode": 7); valor fora da lista volta ao padrão.
        var defaults = new GlobalSettings();
        if (!Enum.IsDefined(config.Global.ConnectionMode))
        {
            config.Global.ConnectionMode = defaults.ConnectionMode;
        }

        if (!Enum.IsDefined(config.Global.WifiMode))
        {
            config.Global.WifiMode = defaults.WifiMode;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        config.Phones.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Serial) || !seen.Add(p.Serial));
        foreach (var phone in config.Phones)
        {
            phone.Settings ??= new PhoneSettings();
            if (phone.Settings.ConnectionMode is { } mode && !Enum.IsDefined(mode))
            {
                phone.Settings.ConnectionMode = null;
            }

            if (phone.Settings.WifiMode is { } wifi && !Enum.IsDefined(wifi))
            {
                phone.Settings.WifiMode = null;
            }
        }
    }
}

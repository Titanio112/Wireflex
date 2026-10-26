using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wireflex.Core.Configuration;
using Wireflex.Core.Logging;

namespace Wireflex.Core.Connection;

/// <summary>
/// Celulares aprendidos (serial, nome, modelo, último IP), gravados no <c>config.json</c> local.
/// Só grava quando algo muda, e só se tiver um <see cref="IConfigStore"/> (o DevConsole simulado não tem).
/// </summary>
/// <remarks>
/// Guarda a própria cópia da configuração lida do disco. Mudanças que o usuário faz só em memória
/// (ex: <c>--modo</c> no DevConsole) não vão parar no arquivo.
/// </remarks>
public sealed class PhoneCatalog
{
    private readonly WireflexConfig _config;
    private readonly IConfigStore? _store;
    private readonly ILogger<PhoneCatalog> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public PhoneCatalog(WireflexConfig stored, IConfigStore? store, ILogger<PhoneCatalog>? logger = null)
    {
        _config = stored ?? throw new ArgumentNullException(nameof(stored));
        _store = store;
        _logger = logger ?? NullLogger<PhoneCatalog>.Instance;
    }

    /// <summary>Cópia dos celulares conhecidos.</summary>
    public IReadOnlyList<KnownPhone> Phones
    {
        get
        {
            lock (_sync)
            {
                return _config.Phones.Select(Copy).ToList();
            }
        }
    }

    public KnownPhone? Find(string serial)
    {
        lock (_sync)
        {
            return _config.FindPhone(serial) is { } phone ? Copy(phone) : null;
        }
    }

    /// <summary>Modo de conexão global (vale para quem não tem um próprio).</summary>
    public ConnectionMode GlobalConnectionMode
    {
        get
        {
            lock (_sync)
            {
                return _config.Global.ConnectionMode;
            }
        }
    }

    /// <summary>Modo de conexão do celular, ou o global se ele não tiver um próprio.</summary>
    public ConnectionMode ResolveConnectionMode(string serial)
    {
        lock (_sync)
        {
            return _config.ResolveConnectionMode(serial);
        }
    }

    public WifiMode ResolveWifiMode(string serial)
    {
        lock (_sync)
        {
            return _config.ResolveWifiMode(serial);
        }
    }

    /// <summary>Nome para a janela do scrcpy: o nome do aparelho, o modelo ou o serial mascarado.</summary>
    public string DisplayName(string serial)
    {
        var phone = Find(serial);
        return phone?.FriendlyName ?? phone?.Model ?? "celular " + Sensitive.Mask(serial);
    }

    /// <summary>
    /// <see cref="DisplayName"/> para o log: o nome do aparelho costuma ter o nome do dono, então vira um marcador.
    /// </summary>
    public string DisplayNameForLog(string serial)
    {
        var phone = Find(serial);
        return phone?.FriendlyName is not null ? "[nome do aparelho]" : phone?.Model ?? "celular " + Sensitive.Mask(serial);
    }

    /// <summary>Registra o que se sabe do celular. Valores <c>null</c> não apagam o que já existe.</summary>
    /// <returns><c>true</c> se algo mudou.</returns>
    public async Task<bool> LearnAsync(string serial, string? model = null, string? friendlyName = null, string? ip = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);

        lock (_sync)
        {
            var phone = _config.FindPhone(serial);
            var changed = false;
            if (phone is null)
            {
                phone = new KnownPhone { Serial = serial };
                _config.Phones.Add(phone);
                changed = true;
            }

            changed |= Update(phone.Model, model, v => phone.Model = v);
            changed |= Update(phone.FriendlyName, friendlyName, v => phone.FriendlyName = v);
            changed |= Update(phone.LastKnownIp, ip, v => phone.LastKnownIp = v);
            if (!changed)
            {
                return false;
            }
        }

        _logger.LogInformation("Celular aprendido: {Phone}", Sensitive.Mask(serial));
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Remove só este celular (nome, IP, ajustes). <c>false</c> se ele não estava na config.</summary>
    public async Task<bool> ForgetAsync(string serial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        lock (_sync)
        {
            if (_config.Phones.RemoveAll(p => p.Serial == serial) == 0)
            {
                return false;
            }
        }

        _logger.LogInformation("Celular esquecido: {Phone}", Sensitive.Mask(serial));
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Modo de conexão próprio do celular (<c>null</c> = segue o global). Cria a entrada se o celular ainda não
    /// estiver na config (ex: só apareceu pelo Wi-Fi).
    /// </summary>
    public async Task SetPhoneConnectionModeAsync(string serial, ConnectionMode? mode, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        lock (_sync)
        {
            var phone = _config.FindPhone(serial);
            if (phone is null)
            {
                phone = new KnownPhone { Serial = serial };
                _config.Phones.Add(phone);
            }
            else if (phone.Settings.ConnectionMode == mode)
            {
                return;
            }

            phone.Settings.ConnectionMode = mode;
        }

        _logger.LogInformation("Modo de conexão de {Phone}: {Mode}", Sensitive.Mask(serial), mode?.ToString() ?? "o global");
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opção "iniciar com o Windows" gravada na config.</summary>
    public bool StartWithWindows
    {
        get
        {
            lock (_sync)
            {
                return _config.Global.StartWithWindows;
            }
        }
    }

    public async Task SetStartWithWindowsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_config.Global.StartWithWindows == enabled)
            {
                return;
            }

            _config.Global.StartWithWindows = enabled;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Muda o modo de conexão global e grava.</summary>
    public async Task SetGlobalConnectionModeAsync(ConnectionMode mode, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_config.Global.ConnectionMode == mode)
            {
                return;
            }

            _config.Global.ConnectionMode = mode;
        }

        _logger.LogInformation("Modo de conexão global: {Mode}", mode);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Grava o estado atual. A cópia é tirada já com a trava de gravação: com vários gravando ao mesmo tempo
    /// (bandeja e coordenador), a última gravação sempre leva o estado mais novo.
    /// </summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_store is null)
        {
            return;
        }

        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WireflexConfig snapshot;
            lock (_sync)
            {
                snapshot = Clone(_config);
            }

            await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Não foi possível gravar a configuração");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private static bool Update(string? current, string? value, Action<string> set)
    {
        if (string.IsNullOrWhiteSpace(value) || value == current)
        {
            return false;
        }

        set(value);
        return true;
    }

    private static KnownPhone Copy(KnownPhone phone) => new()
    {
        Serial = phone.Serial,
        FriendlyName = phone.FriendlyName,
        Model = phone.Model,
        LastKnownIp = phone.LastKnownIp,
        Settings = new PhoneSettings { ConnectionMode = phone.Settings.ConnectionMode, WifiMode = phone.Settings.WifiMode },
    };

    private static WireflexConfig Clone(WireflexConfig config) => new()
    {
        SchemaVersion = config.SchemaVersion,
        Global = new GlobalSettings
        {
            ConnectionMode = config.Global.ConnectionMode,
            WifiMode = config.Global.WifiMode,
            StartWithWindows = config.Global.StartWithWindows,
            AdbPath = config.Global.AdbPath,
            ScrcpyPath = config.Global.ScrcpyPath,
        },
        Phones = config.Phones.Select(Copy).ToList(),
    };
}

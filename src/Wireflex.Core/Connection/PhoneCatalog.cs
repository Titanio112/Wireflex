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

    /// <summary>Registra o que se sabe do celular. Valores <c>null</c> não apagam o que já existe.</summary>
    /// <returns><c>true</c> se algo mudou.</returns>
    public async Task<bool> LearnAsync(string serial, string? model = null, string? friendlyName = null, string? ip = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);

        WireflexConfig? snapshot = null;
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

            if (_store is not null)
            {
                snapshot = Clone(_config);
            }
        }

        _logger.LogInformation("Celular aprendido: {Phone}", Sensitive.Mask(serial));
        if (snapshot is not null)
        {
            await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _store!.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
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

        return true;
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

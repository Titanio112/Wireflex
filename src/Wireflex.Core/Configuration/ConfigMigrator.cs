using System.Text.Json.Nodes;

namespace Wireflex.Core.Configuration;

/// <summary>
/// Atualiza o JSON da configuração até <see cref="WireflexConfig.CurrentSchemaVersion"/>, um passo por versão.
/// Para uma versão nova: aumente <c>CurrentSchemaVersion</c> e adicione o passo <c>N -&gt; N+1</c> em <see cref="Steps"/>.
/// </summary>
public static class ConfigMigrator
{
    public const string VersionProperty = "schemaVersion";

    private static readonly Dictionary<int, Action<JsonObject>> Steps = new()
    {
        // 0 = arquivo sem schemaVersion (antes da v2.0). A estrutura é a mesma da versão 1.
        [0] = _ => { },
    };

    /// <returns>A versão em que o arquivo estava antes da migração.</returns>
    /// <exception cref="UnsupportedConfigVersionException">Arquivo de uma versão mais nova do app.</exception>
    public static int Migrate(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var original = ReadVersion(root);
        if (original > WireflexConfig.CurrentSchemaVersion)
        {
            throw new UnsupportedConfigVersionException(original);
        }

        for (var version = original; version < WireflexConfig.CurrentSchemaVersion; version++)
        {
            if (!Steps.TryGetValue(version, out var step))
            {
                throw new InvalidOperationException($"Falta a migração da configuração {version} -> {version + 1}.");
            }

            step(root);
            root[VersionProperty] = version + 1;
        }

        return original;
    }

    private static int ReadVersion(JsonObject root)
    {
        if (!root.TryGetPropertyValue(VersionProperty, out var node) || node is null)
        {
            return 0;
        }

        return node is JsonValue value && value.TryGetValue<int>(out var version) && version >= 0
            ? version
            : throw new System.Text.Json.JsonException($"'{VersionProperty}' inválido.");
    }
}

public sealed class UnsupportedConfigVersionException(int version)
    : Exception($"A configuração é da versão {version}, mais nova que a suportada ({WireflexConfig.CurrentSchemaVersion}).")
{
    public int Version { get; } = version;
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Wireflex.Core.Configuration;

namespace Wireflex.Tests.Configuration;

// Sempre numa pasta temporária própria; nunca toca o %APPDATA%\Wireflex real.
public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "wireflex-tests-" + Guid.NewGuid().ToString("N"));
    private readonly JsonConfigStore _store;

    public JsonConfigStoreTests()
    {
        _store = new JsonConfigStore(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_store.FilePath, json);
    }

    private string[] FileNames() =>
        Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    [Fact]
    public async Task Load_MissingFile_CreatesDefault()
    {
        var config = await _store.LoadAsync();

        Assert.Equal(WireflexConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal(ConnectionMode.Automatic, config.Global.ConnectionMode);
        Assert.Equal(WifiMode.Automatic, config.Global.WifiMode);
        Assert.False(config.Global.StartWithWindows);
        Assert.Empty(config.Phones);
        Assert.True(File.Exists(_store.FilePath));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip()
    {
        var original = new WireflexConfig
        {
            Global = new GlobalSettings
            {
                ConnectionMode = ConnectionMode.Ask,
                WifiMode = WifiMode.Tcpip,
                StartWithWindows = true,
                AdbPath = @"C:\Tools\adb.exe",
            },
            Phones =
            [
                new KnownPhone
                {
                    Serial = "TESTSERIAL01",
                    FriendlyName = "Celular de teste",
                    Model = "Test Phone",
                    LastKnownIp = "192.0.2.10",
                    Settings = new PhoneSettings { ConnectionMode = ConnectionMode.Never, WifiMode = WifiMode.WirelessDebugging },
                },
            ],
        };

        await _store.SaveAsync(original);
        var loaded = await new JsonConfigStore(_directory).LoadAsync();

        Assert.Equal(ConnectionMode.Ask, loaded.Global.ConnectionMode);
        Assert.Equal(WifiMode.Tcpip, loaded.Global.WifiMode);
        Assert.True(loaded.Global.StartWithWindows);
        Assert.Equal(@"C:\Tools\adb.exe", loaded.Global.AdbPath);

        var phone = Assert.Single(loaded.Phones);
        Assert.Equal("TESTSERIAL01", phone.Serial);
        Assert.Equal("Celular de teste", phone.FriendlyName);
        Assert.Equal("Test Phone", phone.Model);
        Assert.Equal("192.0.2.10", phone.LastKnownIp);
        Assert.Equal(ConnectionMode.Never, phone.Settings.ConnectionMode);
        Assert.Equal(WifiMode.WirelessDebugging, phone.Settings.WifiMode);
    }

    [Fact]
    public async Task Save_WritesSchemaVersionAndEnumsAsText_WithoutTemporaryFile()
    {
        await _store.SaveAsync(new WireflexConfig { SchemaVersion = 0, Global = new GlobalSettings { ConnectionMode = ConnectionMode.Ask } });

        var json = JsonNode.Parse(await File.ReadAllTextAsync(_store.FilePath))!;
        Assert.Equal(WireflexConfig.CurrentSchemaVersion, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("Ask", json["global"]!["connectionMode"]!.GetValue<string>());
        Assert.Equal(new[] { JsonConfigStore.FileName }, FileNames());
    }

    [Fact]
    public void Resolve_PhoneSettingsOverrideGlobal()
    {
        var config = new WireflexConfig
        {
            Global = new GlobalSettings { ConnectionMode = ConnectionMode.Automatic, WifiMode = WifiMode.Tcpip },
            Phones =
            [
                new KnownPhone { Serial = "TESTSERIAL01", Settings = new PhoneSettings { ConnectionMode = ConnectionMode.Never, WifiMode = WifiMode.WirelessDebugging } },
                new KnownPhone { Serial = "TESTSERIAL02" },
            ],
        };

        Assert.Equal(ConnectionMode.Never, config.ResolveConnectionMode("TESTSERIAL01"));
        Assert.Equal(WifiMode.WirelessDebugging, config.ResolveWifiMode("TESTSERIAL01"));

        // Sem valor próprio: usa o global.
        Assert.Equal(ConnectionMode.Automatic, config.ResolveConnectionMode("TESTSERIAL02"));
        Assert.Equal(WifiMode.Tcpip, config.ResolveWifiMode("TESTSERIAL02"));

        // Celular desconhecido: usa o global.
        Assert.Equal(ConnectionMode.Automatic, config.ResolveConnectionMode("TESTSERIAL99"));
    }

    [Fact]
    public async Task Resolve_GlobalNeverWithPhoneAutomatic()
    {
        WriteRaw("""
            {
              "schemaVersion": 1,
              "global": { "connectionMode": "Never" },
              "phones": [ { "serial": "TESTSERIAL01", "settings": { "connectionMode": "Automatic" } } ]
            }
            """);

        var config = await _store.LoadAsync();

        Assert.Equal(ConnectionMode.Automatic, config.ResolveConnectionMode("TESTSERIAL01"));
        Assert.Equal(ConnectionMode.Never, config.ResolveConnectionMode("TESTSERIAL02"));
    }

    [Fact]
    public async Task Load_CorruptFile_KeepsBackupAndRecreatesDefault()
    {
        const string corrupt = "{ isto não é json";
        WriteRaw(corrupt);

        var config = await _store.LoadAsync();

        Assert.Equal(ConnectionMode.Automatic, config.Global.ConnectionMode);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(_store.BackupPath));
        var rewritten = JsonNode.Parse(await File.ReadAllTextAsync(_store.FilePath))!;
        Assert.Equal(WireflexConfig.CurrentSchemaVersion, rewritten["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Load_InvalidEnumText_IsTreatedAsCorrupt()
    {
        WriteRaw("""{ "schemaVersion": 1, "global": { "connectionMode": "Talvez" } }""");

        var config = await _store.LoadAsync();

        Assert.Equal(ConnectionMode.Automatic, config.Global.ConnectionMode);
        Assert.True(File.Exists(_store.BackupPath));
    }

    [Fact]
    public async Task Load_WithoutSchemaVersion_MigratesKeepingValues()
    {
        WriteRaw("""{ "global": { "connectionMode": "Never" }, "phones": [ { "serial": "TESTSERIAL01" } ] }""");

        var config = await _store.LoadAsync();

        Assert.Equal(WireflexConfig.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal(ConnectionMode.Never, config.Global.ConnectionMode);
        Assert.Equal("TESTSERIAL01", Assert.Single(config.Phones).Serial);
        Assert.True(File.Exists(_store.FilePath + ".v0.bak"));
        Assert.False(File.Exists(_store.BackupPath));

        var rewritten = JsonNode.Parse(await File.ReadAllTextAsync(_store.FilePath))!;
        Assert.Equal(WireflexConfig.CurrentSchemaVersion, rewritten["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Load_NewerSchemaVersion_KeepsBackupAndUsesDefault()
    {
        WriteRaw("""{ "schemaVersion": 99, "global": { "connectionMode": "Never" } }""");

        var config = await _store.LoadAsync();

        Assert.Equal(ConnectionMode.Automatic, config.Global.ConnectionMode);
        Assert.Contains("99", await File.ReadAllTextAsync(_store.BackupPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Load_DropsPhonesWithoutSerialAndDuplicates()
    {
        WriteRaw("""
            { "schemaVersion": 1, "phones": [
                { "serial": "TESTSERIAL01", "friendlyName": "primeiro" },
                { "serial": "" },
                { "serial": "TESTSERIAL01", "friendlyName": "repetido" },
                { "serial": "TESTSERIAL02", "settings": null }
            ] }
            """);

        var config = await _store.LoadAsync();

        Assert.Equal(new[] { "TESTSERIAL01", "TESTSERIAL02" }, config.Phones.Select(p => p.Serial));
        Assert.Equal("primeiro", config.Phones[0].FriendlyName);
        Assert.NotNull(config.Phones[1].Settings);
    }

    // Regressão: chave repetida no topo fazia o JsonObject lançar ArgumentException, que escapava.
    [Fact]
    public async Task Load_DuplicateTopLevelKey_KeepsBackupAndRecreatesDefault()
    {
        const string duplicated = """{ "schemaVersion": 1, "global": { "connectionMode": "Never" }, "global": {} }""";
        WriteRaw(duplicated);

        var config = await _store.LoadAsync();

        Assert.Equal(ConnectionMode.Automatic, config.Global.ConnectionMode);
        Assert.Equal(duplicated, await File.ReadAllTextAsync(_store.BackupPath));
    }

    [Fact]
    public async Task TryRead_DuplicateTopLevelKey_ReturnsNullWithoutWriting()
    {
        const string duplicated = """{ "schemaVersion": 1, "schemaVersion": 1 }""";
        WriteRaw(duplicated);

        var config = await _store.TryReadAsync();

        Assert.Null(config);
        Assert.Equal(new[] { JsonConfigStore.FileName }, FileNames());
        Assert.Equal(duplicated, await File.ReadAllTextAsync(_store.FilePath));
    }

    // Regressão: o conversor de enum aceita números; valor fora da lista virava "automático" escondido.
    [Fact]
    public async Task Load_OutOfRangeEnumNumbers_FallBackToDefaults()
    {
        WriteRaw("""
            { "schemaVersion": 1,
              "global": { "connectionMode": 7, "wifiMode": 42 },
              "phones": [ { "serial": "TESTSERIAL01", "settings": { "connectionMode": 9, "wifiMode": "Tcpip" } } ] }
            """);

        var config = await _store.LoadAsync();

        Assert.Equal(ConnectionMode.Automatic, config.Global.ConnectionMode);
        Assert.Equal(WifiMode.Automatic, config.Global.WifiMode);
        var phone = Assert.Single(config.Phones);
        Assert.Null(phone.Settings.ConnectionMode);
        Assert.Equal(WifiMode.Tcpip, phone.Settings.WifiMode);
    }

    [Fact]
    public async Task Load_ValidEnumNumber_IsAccepted()
    {
        WriteRaw("""{ "schemaVersion": 1, "global": { "connectionMode": 2 } }""");

        var config = await _store.LoadAsync();

        Assert.Equal(ConnectionMode.Never, config.Global.ConnectionMode);
    }

    [Fact]
    public async Task TryRead_MissingFile_ReturnsNullAndCreatesNothing()
    {
        var config = await _store.TryReadAsync();

        Assert.Null(config);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task TryRead_CorruptOrOldFile_NeverWrites()
    {
        WriteRaw("{ quebrado");
        Assert.Null(await _store.TryReadAsync());

        const string old = """{ "global": { "connectionMode": "Ask" } }""";
        WriteRaw(old);
        var migratedInMemory = await _store.TryReadAsync();

        Assert.NotNull(migratedInMemory);
        Assert.Equal(ConnectionMode.Ask, migratedInMemory.Global.ConnectionMode);
        Assert.Equal(old, await File.ReadAllTextAsync(_store.FilePath));
        Assert.Equal(new[] { JsonConfigStore.FileName }, FileNames());
    }

    [Fact]
    public async Task Load_AllowsCommentsAndTrailingCommas()
    {
        WriteRaw("""
            {
              // comentário do usuário
              "schemaVersion": 1,
              "global": { "connectionMode": "Ask", },
            }
            """);

        var config = await _store.LoadAsync();

        Assert.Equal(ConnectionMode.Ask, config.Global.ConnectionMode);
        Assert.False(File.Exists(_store.BackupPath));
    }
}

public class ConfigMigratorTests
{
    [Fact]
    public void Migrate_MissingVersion_IsVersionZeroAndBecomesCurrent()
    {
        var root = new JsonObject();

        Assert.Equal(0, ConfigMigrator.Migrate(root));
        Assert.Equal(WireflexConfig.CurrentSchemaVersion, root[ConfigMigrator.VersionProperty]!.GetValue<int>());
    }

    [Fact]
    public void Migrate_CurrentVersion_ChangesNothing()
    {
        var root = new JsonObject { [ConfigMigrator.VersionProperty] = WireflexConfig.CurrentSchemaVersion };

        Assert.Equal(WireflexConfig.CurrentSchemaVersion, ConfigMigrator.Migrate(root));
    }

    [Fact]
    public void Migrate_NewerVersion_Throws()
    {
        var root = new JsonObject { [ConfigMigrator.VersionProperty] = WireflexConfig.CurrentSchemaVersion + 1 };

        var ex = Assert.Throws<UnsupportedConfigVersionException>(() => ConfigMigrator.Migrate(root));
        Assert.Equal(WireflexConfig.CurrentSchemaVersion + 1, ex.Version);
    }

    [Theory]
    [InlineData("\"um\"")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void Migrate_InvalidVersion_ThrowsJsonException(string versionJson)
    {
        var root = JsonNode.Parse($$"""{ "schemaVersion": {{versionJson}} }""")!.AsObject();

        Assert.Throws<JsonException>(() => ConfigMigrator.Migrate(root));
    }
}

using Microsoft.Extensions.Logging;
using Wireflex.Core.Configuration;
using Wireflex.Core.Connection;
using Wireflex.Core.Logging;

namespace Wireflex.Tests.Logging;

public class SensitiveTests
{
    [Theory]
    [InlineData("TESTSERIAL01", "***AL01")]
    [InlineData("ABCD", "***")]
    [InlineData("", "(vazio)")]
    [InlineData(null, "(vazio)")]
    public void Mask_ShowsOnlyLastFourCharacters(string? value, string expected)
    {
        Assert.Equal(expected, Sensitive.Mask(value));
    }

    [Theory]
    [InlineData("TESTSERIAL01", "***AL01")]
    [InlineData("192.0.2.10:5555", "***2.10:5555")]
    [InlineData("adb-TESTSERIAL02-AbCdEf._adb-tls-connect._tcp", "mdns:***AL02")]
    [InlineData("emulator-5554", "emulator-5554")]
    public void MaskAdbSerial_ByFormat(string adbSerial, string expected)
    {
        Assert.Equal(expected, Sensitive.MaskAdbSerial(adbSerial));
    }

    [Theory]
    [InlineData("conectado em 192.0.2.10:5555", "conectado em ***2.10:5555")]
    [InlineData("falhou em 192.0.2.10.", "falhou em ***2.10.")]
    [InlineData("ip 192.0.2.25. fim", "ip ***2.25. fim")]
    public void ScrubIpAddresses_MasksIpv4IncludingEndOfSentence(string text, string expected)
    {
        Assert.Equal(expected, Sensitive.ScrubIpAddresses(text));
    }

    [Theory]
    [InlineData("[2001:db8::1]:5555 erro", "2001:db8")]
    [InlineData("endereço 2001:db8:0:0:0:0:0:1 visto", "2001:db8")]
    [InlineData("host 2001:db8::a1b2:c3d4", "2001:db8")]
    public void ScrubIpAddresses_MasksIpv6(string text, string mustNotContain)
    {
        var scrubbed = Sensitive.ScrubIpAddresses(text);

        Assert.DoesNotContain(mustNotContain, scrubbed, StringComparison.Ordinal);
        Assert.Contains("***", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-10-24 12:40:16.696 [INF] ok")]
    [InlineData("adb 37.0.0-14910828")]
    [InlineData("1.2.3.4.5 não é IP")]
    public void ScrubIpAddresses_LeavesNonAddressesAlone(string text)
    {
        Assert.Equal(text, Sensitive.ScrubIpAddresses(text));
    }

    [Theory]
    [InlineData(@"C:\Users\TestUser\AppData\Roaming\Wireflex", @"C:\Users\%USERNAME%\AppData\Roaming\Wireflex")]
    [InlineData(@"D:\testuser\Documents\adb.exe", @"D:\%USERNAME%\Documents\adb.exe")]
    [InlineData(@"pasta C:\Users\TestUser fim", @"pasta C:\Users\%USERNAME% fim")]
    [InlineData(@"C:\Users\TestUserX\AppData", @"C:\Users\TestUserX\AppData")]
    [InlineData("TestUser sem caminho", "TestUser sem caminho")]
    public void ScrubUserName_ReplacesUserFolderInPaths(string text, string expected)
    {
        Assert.Equal(expected, Sensitive.ScrubUserName(text, "TestUser"));
    }

    [Fact]
    public void ScrubUserName_EmptyUserName_LeavesTextAlone()
    {
        Assert.Equal(@"C:\Users\TestUser", Sensitive.ScrubUserName(@"C:\Users\TestUser", " "));
    }

    [Fact]
    public void ScrubKnown_MasksKnownSerialsInThirdPartyOutput()
    {
        var line = "INFO: ADB device found:\n    -->   (usb)  TESTSERIAL01            device  Test_Phone\n    (tcpip) 192.0.2.10:5555";

        var scrubbed = Sensitive.ScrubKnown(line, "TESTSERIAL01", "192.0.2.10:5555", null);

        Assert.DoesNotContain("TESTSERIAL01", scrubbed);
        Assert.DoesNotContain("192.0.2.10", scrubbed);
        Assert.Contains("***AL01", scrubbed);
    }

    // Seriais fictícios só destes testes (a lista de seriais guardados vale para o processo inteiro).
    private const string ScrcpyDeviceList =
        "INFO: ADB device found:\n"
        + "INFO:     -->   (usb)  TESTSERIAL31                    device  Modelo_Alfa\n"
        + "INFO:           (usb)  TESTSERIAL32                    device  Modelo_Beta\n"
        + "INFO:           (tcpip)  192.0.2.33:5555               device  Modelo_Gama\n"
        + "INFO:           (usb)  TESTSERIAL34                    unauthorized\n"
        + "INFO:           (tcpip)  adb-TESTSERIAL35-AbCdEf._adb-tls-connect._tcp  offline";

    [Fact]
    public void Scrub_ListaDoScrcpyComVariosCelulares_MascaraTodos()
    {
        var scrubbed = Sensitive.Scrub(ScrcpyDeviceList);

        foreach (var value in (string[])["TESTSERIAL31", "TESTSERIAL32", "192.0.2.33", "TESTSERIAL34", "TESTSERIAL35"])
        {
            Assert.DoesNotContain(value, scrubbed, StringComparison.Ordinal);
        }

        Assert.Contains("(usb)  ***AL31                    device  Modelo_Alfa", scrubbed, StringComparison.Ordinal);
        Assert.Contains("(usb)  ***AL32", scrubbed, StringComparison.Ordinal);
        Assert.Contains("(tcpip)  ***2.33:5555", scrubbed, StringComparison.Ordinal);
        Assert.Contains("(usb)  ***AL34                    unauthorized", scrubbed, StringComparison.Ordinal);
        Assert.Contains("mdns:***AL35", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_LinhaDeLogDoScrcpy_MascaraOSerialDeOutroCelular()
    {
        // O formato da linha que vazava: o serial do celular da linha mascarado, o do outro celular inteiro.
        var line = "2026-01-01 10:00:00.000 [INF] ScrcpyLauncher: scrcpy[1234] ***AL01: INFO:           (usb)  TESTSERIAL36   device  Modelo_Alfa";

        var scrubbed = Sensitive.Scrub(line);

        Assert.DoesNotContain("TESTSERIAL36", scrubbed, StringComparison.Ordinal);
        Assert.EndsWith("(usb)  ***AL36   device  Modelo_Alfa", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrub_SaidaDoAdbDevices_MascaraCadaLinha()
    {
        var output = "List of devices attached\n"
            + "TESTSERIAL41\tdevice product:alfa model:Modelo_Alfa device:alfa transport_id:1\n"
            + "192.0.2.42:5555\toffline\n"
            + "TESTSERIAL43\tunauthorized usb:1-2 transport_id:3\n";

        var scrubbed = Sensitive.Scrub(output);

        Assert.Equal(
            "List of devices attached\n"
            + "***AL41\tdevice product:alfa model:Modelo_Alfa device:alfa transport_id:1\n"
            + "***2.42:5555\toffline\n"
            + "***AL43\tunauthorized usb:1-2 transport_id:3\n",
            scrubbed);
    }

    [Theory]
    [InlineData("adb: device 'TESTSERIAL44' not found", "adb: device '***AL44' not found")]
    [InlineData("O adb recusou: no such device '192.0.2.45:5555'", "O adb recusou: no such device '***2.45:5555'")]
    [InlineData("adb -s TESTSERIAL46 shell getprop", "adb -s ***AL46 shell getprop")]
    [InlineData("scrcpy --serial=TESTSERIAL47 --port=27200", "scrcpy --serial=***AL47 --port=27200")]
    [InlineData("scrcpy --serial \"TESTSERIAL48\"", "scrcpy --serial \"***AL48\"")]
    [InlineData("ANDROID_SERIAL=TESTSERIAL49 adb shell", "ANDROID_SERIAL=***AL49 adb shell")]
    public void Scrub_SeriaisPeloFormato_EmMensagensDoAdbEArgumentos(string text, string expected)
    {
        Assert.Equal(expected, Sensitive.Scrub(text));
    }

    [Theory]
    [InlineData("ScrcpyLauncher: scrcpy[1] ***AL01: INFO:     -->   (usb)  ***AL01   device  Modelo_Alfa")]
    [InlineData("Abrindo o scrcpy: --serial=***AL01 --window-title \"Wireflex - [nome do aparelho]\" --port=27200")]
    [InlineData("Apareceu ***AL01 via Usb (Device)")]
    [InlineData("Ação: AbrirScrcpy ***AL01 via ***2.10:5555 (tcpip) (conexão ***2.10:5555)")]
    [InlineData("Celular offline há 5 s; device pronto")]
    public void Scrub_TextoJaMascaradoOuComum_NaoMuda(string text)
    {
        Assert.Equal(text, Sensitive.Scrub(text));
    }

    [Fact]
    public void RememberSerial_MascaraOSerialEmQualquerTexto()
    {
        Sensitive.RememberSerial("TESTSERIAL51");

        var scrubbed = Sensitive.Scrub("System.IO.IOException: falhou ao falar com TESTSERIAL51.\n   at Wireflex.Teste()");

        Assert.Equal("System.IO.IOException: falhou ao falar com ***AL51.\n   at Wireflex.Teste()", scrubbed);
    }

    [Fact]
    public void RememberSerial_Mdns_GuardaOSerialDeDentroDoNome()
    {
        Sensitive.RememberSerial("adb-TESTSERIAL52-AbCdEf._adb-tls-connect._tcp");

        Assert.Equal("visto ***AL52", Sensitive.Scrub("visto TESTSERIAL52"));
    }

    [Theory]
    [InlineData("AB12")]
    [InlineData("emulator-5554")]
    [InlineData("***AL53")]
    public void RememberSerial_IgnoraValoresCurtosEmuladorEJaMascarados(string value)
    {
        Sensitive.RememberSerial(value);

        Assert.Equal($"texto {value} fim", Sensitive.Scrub($"texto {value} fim"));
    }

    [Fact]
    public void PhoneCatalog_GuardaOsSeriaisDaConfig()
    {
        var config = new WireflexConfig();
        config.Phones.Add(new KnownPhone { Serial = "TESTSERIAL54" });

        _ = new PhoneCatalog(config, store: null);

        Assert.Equal("lista: ***AL54", Sensitive.Scrub("lista: TESTSERIAL54"));
    }

    [Fact]
    public void FileLogger_SaidaDoScrcpyComVariosCelulares_NaoGravaSerialNemIp()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Wireflex.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            using (var provider = new FileLoggerProvider(directory))
            {
                path = provider.CurrentFilePath;
                var logger = provider.CreateLogger("Wireflex.Core.Scrcpy.ScrcpyLauncher");
                foreach (var line in ScrcpyDeviceList.Split('\n'))
                {
                    logger.LogInformation("scrcpy[{Pid}] {Phone}: {Line}", 1234, "***AL31", line);
                }

                logger.LogWarning(new IOException("no such device 'TESTSERIAL32'"), "Falha com {Phone}", "***AL32");
            }

            var text = File.ReadAllText(path);
            foreach (var value in (string[])["TESTSERIAL31", "TESTSERIAL32", "192.0.2.33", "TESTSERIAL34", "TESTSERIAL35"])
            {
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            }

            Assert.Contains("Modelo_Alfa", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Scrub_UsesCurrentWindowsUser()
    {
        var path = Path.Combine(@"C:\Users", Environment.UserName, "AppData");

        var scrubbed = Sensitive.Scrub(path + " em 192.0.2.10");

        Assert.Equal(@"C:\Users\%USERNAME%\AppData em ***2.10", scrubbed);
    }
}

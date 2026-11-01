using Wireflex.Core.Adb;
using Wireflex.Core.Decisions;
using Wireflex.Core.Tray;

namespace Wireflex.Tests.Tray;

public sealed class QuestionQueueTests
{
    private const string PhoneA = "TESTSERIAL01";
    private const string PhoneB = "TESTSERIAL02";

    private readonly QuestionQueue _queue = new();

    [Fact]
    public void UmaPerguntaDeCadaVez()
    {
        Assert.True(_queue.Enqueue(PhoneA));
        Assert.False(_queue.Enqueue(PhoneB));

        Assert.Equal(PhoneA, _queue.Current);
        Assert.Equal(PhoneB, _queue.Remove(PhoneA));
        Assert.Equal(PhoneB, _queue.Current);
    }

    [Fact]
    public void MesmoCelular_NaoRepete()
    {
        _queue.Enqueue(PhoneA);

        Assert.False(_queue.Enqueue(PhoneA));
        Assert.Single(_queue.Pending);
    }

    [Fact]
    public void RespondidaPeloMenuForaDaVez_NaoTrocaODialogo()
    {
        _queue.Enqueue(PhoneA);
        _queue.Enqueue(PhoneB);

        Assert.Null(_queue.Remove(PhoneB));
        Assert.Equal(PhoneA, _queue.Current);
    }

    [Fact]
    public void QuemNaoEsperaMais_SaiEOdialogoTroca()
    {
        _queue.Enqueue(PhoneA);
        _queue.Enqueue(PhoneB);

        // Ex: PhoneA saiu do alcance ou abriu pelo "Abrir agora".
        Assert.True(_queue.Prune(serial => serial == PhoneB));
        Assert.Equal(PhoneB, _queue.Current);

        Assert.False(_queue.Prune(_ => true));
    }

    [Fact]
    public void Textos_SemDadosPessoais()
    {
        Assert.Equal("Celular Alfa conectou pelo Wi-Fi. Abrir o espelhamento?", TrayTexts.Question("Celular Alfa", AdbTransport.WifiTcpip));
        Assert.Contains("autorize", TrayTexts.Notice("Celular Alfa", DeviceNotice.Unauthorized));
        Assert.Contains("offline", TrayTexts.Notice("Celular Alfa", DeviceNotice.Offline));
        Assert.Equal("O espelhamento de Celular Alfa não abriu: erro", TrayTexts.ScrcpyFailed("Celular Alfa", "erro"));
    }
}

using System.Globalization;
using System.Text;

namespace Wireflex.Core.Adb;

/// <summary>
/// Separa as mensagens que <c>adb track-devices</c> escreve na saída. Cada mensagem é
/// <c>XXXX</c> (tamanho em hexadecimal, 4 dígitos) seguido do texto da lista completa.
/// Lista vazia chega como <c>0000</c>. Sem processo nem rede: recebe bytes, devolve textos.
/// </summary>
/// <remarks>
/// O <see cref="AdbClient"/> lê direto do socket, que manda os bytes sem alteração. A tolerância abaixo
/// fica para quem ler a saída do adb.exe: no Windows ele escreve o stdout em modo texto (cada <c>\n</c> vira
/// <c>\r\n</c>), mas o cabeçalho continua contando o tamanho original. Por isso todo <c>\r</c> é descartado
/// ao receber (o conteúdo do adb nunca tem <c>\r</c> de verdade) e quebras de linha soltas antes de um
/// cabeçalho são ignoradas.
/// </remarks>
public sealed class TrackDevicesFrameDecoder
{
    private const int HeaderLength = 4;
    private const byte CarriageReturn = (byte)'\r';
    private const byte LineFeed = (byte)'\n';
    private readonly List<byte> _buffer = [];

    public IReadOnlyList<string> Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b != CarriageReturn)
            {
                _buffer.Add(b);
            }
        }

        var payloads = new List<string>();
        while (true)
        {
            SkipLineBreaks();
            SkipDaemonMessages();
            SkipLineBreaks();
            if (_buffer.Count < HeaderLength)
            {
                break;
            }

            var header = Encoding.ASCII.GetString(_buffer.GetRange(0, HeaderLength).ToArray());
            if (!int.TryParse(header, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length))
            {
                throw new AdbException($"Resposta inesperada do adb track-devices (cabeçalho '{header}').");
            }

            if (_buffer.Count < HeaderLength + length)
            {
                break;
            }

            payloads.Add(Encoding.UTF8.GetString(_buffer.GetRange(HeaderLength, length).ToArray()));
            _buffer.RemoveRange(0, HeaderLength + length);
        }

        return payloads;
    }

    /// <summary>Um cabeçalho nunca começa com quebra de linha: as que sobram entre mensagens são descartadas.</summary>
    private void SkipLineBreaks()
    {
        var count = 0;
        while (count < _buffer.Count && _buffer[count] == LineFeed)
        {
            count++;
        }

        _buffer.RemoveRange(0, count);
    }

    /// <summary>
    /// Ignora linhas como <c>* daemon not running; starting now at tcp:5037</c>, caso o adb as escreva na saída padrão.
    /// </summary>
    private void SkipDaemonMessages()
    {
        while (_buffer.Count > 0 && _buffer[0] == (byte)'*')
        {
            var newline = _buffer.IndexOf((byte)'\n');
            if (newline < 0)
            {
                return;
            }

            _buffer.RemoveRange(0, newline + 1);
        }
    }
}

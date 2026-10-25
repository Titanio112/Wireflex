using System.Globalization;
using System.Text;
using Wireflex.Core.Logging;

namespace Wireflex.Core.Adb;

/// <summary>
/// Formato das mensagens trocadas com o servidor do adb (porta 5037).
/// Pedido: tamanho em hexadecimal (4 dígitos ASCII) + serviço (ex: <c>000Chost:version</c>).
/// Resposta: <c>OKAY</c>, ou <c>FAIL</c> + tamanho hex + motivo.
/// </summary>
public static class AdbWire
{
    private const int HeaderLength = 4;
    private const int MaxRequestLength = 0xFFFF;

    public static byte[] EncodeRequest(string service)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);
        var payload = Encoding.UTF8.GetBytes(service);
        if (payload.Length > MaxRequestLength)
        {
            throw new ArgumentException("Pedido grande demais para o adb.", nameof(service));
        }

        var header = Encoding.ASCII.GetBytes(payload.Length.ToString("X4", CultureInfo.InvariantCulture));
        return [.. header, .. payload];
    }

    public static async Task SendRequestAsync(Stream stream, string service, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await stream.WriteAsync(EncodeRequest(service), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lê <c>OKAY</c>. <c>FAIL</c> vira <see cref="AdbException"/> com o motivo (mascarado).</summary>
    public static async Task ReadStatusAsync(Stream stream, string service, CancellationToken cancellationToken)
    {
        var status = Encoding.ASCII.GetString(await ReadExactAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false));
        switch (status)
        {
            case "OKAY":
                return;
            case "FAIL":
                var reason = await ReadLengthPrefixedAsync(stream, cancellationToken).ConfigureAwait(false);
                throw new AdbException($"O adb recusou '{DescribeService(service)}': {Sensitive.Scrub(reason)}");
            default:
                throw new AdbException($"Resposta inesperada do adb para '{DescribeService(service)}' ('{status}').");
        }
    }

    /// <summary>Lê uma resposta no formato tamanho hex (4 dígitos) + texto.</summary>
    public static async Task<string> ReadLengthPrefixedAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = Encoding.ASCII.GetString(await ReadExactAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false));
        if (!int.TryParse(header, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length))
        {
            throw new AdbException($"Resposta inesperada do adb (tamanho '{header}').");
        }

        return Encoding.UTF8.GetString(await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Lê até o servidor fechar a conexão (ex: saída de <c>shell:</c>).</summary>
    public static async Task<string> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Nome do serviço para logs e erros, com serial e IP mascarados.</summary>
    public static string DescribeService(string service)
    {
        foreach (var prefix in (string[])["host:transport:", "host:connect:", "host:disconnect:"])
        {
            if (service.StartsWith(prefix, StringComparison.Ordinal))
            {
                return prefix + Sensitive.MaskAdbSerial(service[prefix.Length..]);
            }
        }

        return Sensitive.Scrub(service);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[count];
        try
        {
            await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new AdbException("O servidor do adb fechou a conexão no meio da resposta.", ex);
        }

        return buffer;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace VppSpy.GoodWe;

public sealed record GoodWeDeviceInfo(string SerialNumber, string ModelName, string RawResponseHex);

public sealed record GoodWeDiscoveryResult(string RemoteAddress, int RemotePort, string RawText, string RawHex);

public sealed class GoodWeCommunicationException(string message) : Exception(message);

/// <summary>
/// Talks to a GoodWe ET/EH/BT/BH-family inverter over its local UDP Modbus-RTU interface (port 8899).
/// Frame layout follows the inverter's undocumented-but-widely-reverse-engineered local protocol:
/// requests are plain Modbus RTU (addr, cmd, offset, count, crc16); responses are prefixed with an
/// extra 2-byte header before the same addr/cmd/len/payload/crc16 structure.
/// </summary>
public sealed class GoodWeModbusClient(IOptions<GoodWeOptions> options, ILogger<GoodWeModbusClient> logger)
{
    private const byte ReadHoldingRegistersCmd = 0x03;
    private const ushort DeviceInfoRegisterOffset = 0x88B8;
    private const ushort DeviceInfoRegisterCount = 0x0021;

    private readonly GoodWeOptions _options = options.Value;

    public async Task<GoodWeDeviceInfo> ReadDeviceInfoAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            throw new GoodWeCommunicationException("GoodWe:Host is not configured in appsettings.json.");
        }

        var request = BuildReadHoldingRegistersRequest(_options.CommAddress, DeviceInfoRegisterOffset, DeviceInfoRegisterCount);

        using var udpClient = new UdpClient();
        udpClient.Connect(_options.Host, _options.Port);

        logger.LogInformation("Sending GoodWe device-info request to {Host}:{Port}", _options.Host, _options.Port);

        using var timeoutCts = new CancellationTokenSource(_options.TimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        UdpReceiveResult result;
        try
        {
            await udpClient.SendAsync(request, linkedCts.Token);
            result = await udpClient.ReceiveAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GoodWeCommunicationException(
                $"No response from GoodWe inverter at {_options.Host}:{_options.Port} within {_options.TimeoutMs}ms.");
        }

        var payload = ParseHoldingRegistersResponse(result.Buffer, ReadHoldingRegistersCmd, DeviceInfoRegisterCount);

        var serialNumber = DecodeAscii(payload, 6, 16);
        var modelName = DecodeAscii(payload, 22, 10);

        return new GoodWeDeviceInfo(serialNumber, modelName, Convert.ToHexString(result.Buffer));
    }

    /// <summary>
    /// Broadcasts the GoodWe WiFi-dongle discovery probe ("WIFIKIT-214028-READ" on UDP 48899) and
    /// collects every reply received within the discovery window. Useful for confirming a dongle is
    /// actually reachable/listening on the LAN and for finding its real IP, independent of whether the
    /// Modbus device-info request works.
    /// </summary>
    public async Task<IReadOnlyList<GoodWeDiscoveryResult>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var probe = Encoding.ASCII.GetBytes("WIFIKIT-214028-READ");

        using var udpClient = new UdpClient();
        udpClient.EnableBroadcast = true;

        using var timeoutCts = new CancellationTokenSource(_options.DiscoveryTimeoutMs);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        logger.LogInformation("Broadcasting GoodWe discovery probe to 255.255.255.255:{Port}", _options.DiscoveryPort);
        await udpClient.SendAsync(probe, new IPEndPoint(IPAddress.Broadcast, _options.DiscoveryPort), linkedCts.Token);

        var results = new List<GoodWeDiscoveryResult>();
        try
        {
            while (true)
            {
                var result = await udpClient.ReceiveAsync(linkedCts.Token);
                results.Add(new GoodWeDiscoveryResult(
                    result.RemoteEndPoint.Address.ToString(),
                    result.RemoteEndPoint.Port,
                    Encoding.ASCII.GetString(result.Buffer),
                    Convert.ToHexString(result.Buffer)));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Discovery window elapsed; return whatever responses were collected.
        }

        return results;
    }

    internal static byte[] BuildReadHoldingRegistersRequest(byte commAddress, ushort offset, ushort count)
    {
        var data = new byte[6];
        data[0] = commAddress;
        data[1] = ReadHoldingRegistersCmd;
        data[2] = (byte)(offset >> 8);
        data[3] = (byte)(offset & 0xFF);
        data[4] = (byte)(count >> 8);
        data[5] = (byte)(count & 0xFF);

        var crc = ModbusCrc16(data);

        var request = new byte[8];
        Array.Copy(data, request, data.Length);
        request[6] = (byte)(crc & 0xFF);
        request[7] = (byte)(crc >> 8);
        return request;
    }

    /// <summary>
    /// Response layout: [0-1] 2-byte header, [2] source addr, [3] cmd, [4] payload length (bytes),
    /// [5 .. 5+len) payload, [len+5, len+6] crc16 (low byte first). CRC covers bytes [2, len+5).
    /// </summary>
    internal static byte[] ParseHoldingRegistersResponse(byte[] response, byte expectedCmd, ushort expectedRegisterCount)
    {
        if (response.Length <= 4)
        {
            throw new GoodWeCommunicationException($"Response too short ({response.Length} bytes).");
        }

        if (response[3] != expectedCmd)
        {
            throw new GoodWeCommunicationException(
                $"Inverter returned command 0x{response[3]:X2}, expected 0x{expectedCmd:X2} (possible error response).");
        }

        var payloadLength = response[4];
        var expectedPayloadLength = expectedRegisterCount * 2;
        if (payloadLength != expectedPayloadLength)
        {
            throw new GoodWeCommunicationException(
                $"Response payload length {payloadLength} does not match expected {expectedPayloadLength} bytes.");
        }

        var expectedTotalLength = payloadLength + 7;
        if (response.Length < expectedTotalLength)
        {
            throw new GoodWeCommunicationException(
                $"Response is incomplete: got {response.Length} bytes, expected {expectedTotalLength}.");
        }

        var checksumOffset = expectedTotalLength - 2;
        var computedCrc = ModbusCrc16(response.AsSpan(2, checksumOffset - 2));
        var receivedCrc = (ushort)(response[checksumOffset] | (response[checksumOffset + 1] << 8));
        if (computedCrc != receivedCrc)
        {
            throw new GoodWeCommunicationException(
                $"Response CRC-16 mismatch: computed 0x{computedCrc:X4}, received 0x{receivedCrc:X4}.");
        }

        return response[5..(5 + payloadLength)];
    }

    private static string DecodeAscii(byte[] payload, int offset, int length)
    {
        if (offset + length > payload.Length)
        {
            return string.Empty;
        }

        return Encoding.ASCII.GetString(payload, offset, length).TrimEnd('\0', ' ');
    }

    private static ushort ModbusCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x0001) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }
}

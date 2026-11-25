using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

sealed record SshProxy(string Kind, string Host, int Port)
{
    // The fields appear in an OpenSSH ProxyCommand and must remain single shell words.
    internal static bool ValidHost(string host) => host.Length is > 0 and <= 253 &&
        (IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6 && host.All(c => char.IsAsciiHexDigit(c) || c is ':' or '.')
         || Regex.IsMatch(host, @"\A[a-zA-Z0-9][a-zA-Z0-9.-]*\z"));

    internal void Validate()
    {
        if (Kind is not ("socks5" or "http") || !ValidHost(Host) || Port is < 1 or > 65535)
            throw new IOException(L.T("请输入有效的代理地址和端口。"));
    }

    internal string BuildOption()
    {
        Validate();
        return $" -o ControlPath=none -o \"ProxyCommand=RsyncDeck.exe --ssh-proxy {Kind} {Host} {Port} %h %p\"";
    }

    internal static async Task<int> Relay(string[] args)
    {
        try
        {
            if (!int.TryParse(args[3], out int port) || !int.TryParse(args[5], out int targetPort)) throw new IOException("Invalid port.");
            var proxy = new SshProxy(args[1], args[2], port);
            using var tunnel = await Connect(proxy, args[4].Trim('[', ']'), targetPort);
            using var toSsh = Console.OpenStandardOutput();
            using var fromSsh = Console.OpenStandardInput();
            using var cancel = new CancellationTokenSource();
            var stream = tunnel.GetStream();
            async Task Send()
            {
                await fromSsh.CopyToAsync(stream, cancel.Token);
                tunnel.Client.Shutdown(SocketShutdown.Send);
            }
            var send = Send();
            var receive = stream.CopyToAsync(toSsh, cancel.Token);
            if (await Task.WhenAny(send, receive) == send) await send;
            await receive;
            await toSsh.FlushAsync();
            await cancel.CancelAsync();
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("RsyncDeck proxy: " + e.Message); return 1; }
    }

    internal static async Task<TcpClient> Connect(SshProxy proxy, string target, int targetPort)
    {
        proxy.Validate();
        if (!ValidHost(target) || targetPort is < 1 or > 65535) throw new IOException("Invalid SSH destination.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(proxy.Host, proxy.Port, timeout.Token);
            if (proxy.Kind == "socks5") await NegotiateSocks(client.GetStream(), target, targetPort, timeout.Token);
            else await NegotiateHttp(client.GetStream(), target, targetPort, timeout.Token);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task NegotiateSocks(Stream stream, string target, int port, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        var hello = new byte[2]; await stream.ReadExactlyAsync(hello, token);
        if (hello[0] != 5 || hello[1] != 0) throw new IOException("SOCKS5 proxy did not accept a connection without authentication.");
        byte[] destination;
        byte type;
        if (IPAddress.TryParse(target, out var address))
        {
            destination = address.GetAddressBytes();
            type = address.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        }
        else { var name = Encoding.ASCII.GetBytes(target); destination = [(byte)name.Length, .. name]; type = 3; }
        byte[] request = [5, 1, 0, type, .. destination, (byte)(port >> 8), (byte)(port & 255)];
        await stream.WriteAsync(request, token);
        var response = new byte[4]; await stream.ReadExactlyAsync(response, token);
        if (response[0] != 5 || response[1] != 0 || response[2] != 0) throw new IOException($"SOCKS5 CONNECT failed (reply {response[1]}).");
        int length = response[3] switch { 1 => 4, 4 => 16, 3 => await ReadByte(stream, token), _ => throw new IOException("Invalid SOCKS5 address type.") };
        await stream.ReadExactlyAsync(new byte[length + 2], token);
    }

    private static async Task<byte> ReadByte(Stream stream, CancellationToken token)
    {
        byte[] one = new byte[1];
        await stream.ReadExactlyAsync(one, token);
        return one[0];
    }

    private static async Task NegotiateHttp(Stream stream, string target, int port, CancellationToken token)
    {
        var authority = target.Contains(':') ? $"[{target}]:{port}" : $"{target}:{port}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"), token);
        byte[] header = new byte[16384];
        for (int i = 0; i < header.Length; i++)
        {
            await stream.ReadExactlyAsync(header.AsMemory(i, 1), token);
            if (i < 3 || !header.AsSpan(i - 3, 4).SequenceEqual("\r\n\r\n"u8)) continue;
            string status = Encoding.ASCII.GetString(header, 0, Array.IndexOf(header, (byte)'\r', 0, i + 1));
            var match = Regex.Match(status, @"\AHTTP/1\.[01] ([0-9]{3})(?: |\z)");
            if (!match.Success || match.Groups[1].Value[0] != '2')
                throw new IOException(match.Success ? $"HTTP CONNECT failed ({match.Groups[1].Value})." : "Invalid HTTP CONNECT response.");
            return;
        }
        throw new IOException("HTTP CONNECT response header is too large.");
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;

static class ProxySelfTest
{
    internal static async Task<int> Run(string report, string rsync, string scratch)
    {
        var results = new List<string>();
        try
        {
            if (!Engine.IsBundledEngine(rsync) || !File.Exists(rsync)) throw new IOException("This test needs the portable engine.");
            if (Engine.ResolveEngine("") != rsync) throw new IOException("Portable engine was not selected automatically.");
            results.Add($"PASS portable engine auto-selected; runtime {Environment.Version}");
            foreach (string kind in new[] { "socks5", "http" })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var proxy = new SshProxy(kind, "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port);
                var profile = new Engine.Profile("Proxy test", scratch, "test@proxy-test.invalid:/fixture/", false, false, [], null);
                var job = await Engine.Validate(profile, rsync, false, 2222, deadline.Token, proxy: proxy);
                var lines = new List<string>();
                var engine = Engine.Run(job, true, true, false, false, [], line => { lock (lines) lines.Add(line); }, deadline.Token, verify: true);
                var answer = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                    using var stream = client.GetStream();
                    var one = new byte[1];
                    async Task<byte[]> Read(int count)
                    {
                        byte[] value = new byte[count]; await stream.ReadExactlyAsync(value, deadline.Token); return value;
                    }
                    if (kind == "socks5")
                    {
                        if (!(await Read(3)).SequenceEqual(new byte[] { 5, 1, 0 })) throw new IOException("Unexpected SOCKS5 greeting.");
                        await stream.WriteAsync(new byte[] { 5, 0 }, deadline.Token);
                        var request = await Read(5);
                        if (!request.AsSpan(0, 4).SequenceEqual(new byte[] { 5, 1, 0, 3 }) || Encoding.ASCII.GetString(await Read(request[4])) != "proxy-test.invalid" || !(await Read(2)).SequenceEqual(new byte[] { 8, 174 }))
                            throw new IOException("Unexpected SOCKS5 destination.");
                        await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, deadline.Token);
                    }
                    else
                    {
                        var header = new StringBuilder();
                        while (!header.ToString().EndsWith("\r\n\r\n") && header.Length < 4096)
                        { await stream.ReadExactlyAsync(one, deadline.Token); header.Append((char)one[0]); }
                        if (!header.ToString().StartsWith("CONNECT proxy-test.invalid:2222 HTTP/1.1\r\n")) throw new IOException("Unexpected HTTP CONNECT destination.");
                        await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), deadline.Token);
                    }
                    var banner = new StringBuilder();
                    while (!banner.ToString().EndsWith('\n') && banner.Length < 255)
                    { await stream.ReadExactlyAsync(one, deadline.Token); banner.Append((char)one[0]); }
                    if (!banner.ToString().StartsWith("SSH-2.0-OpenSSH")) throw new IOException("SSH banner did not reach proxy.");
                    await stream.WriteAsync("SSH-1.5-RsyncDeckFixture\r\n"u8.ToArray(), deadline.Token);
                }, deadline.Token);
                try { await Task.WhenAll(answer, engine); }
                catch { results.AddRange(lines); throw; }
                if (!engine.Result.Failed || !lines.Any(x => x.Contains("major versions differ", StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("SSH did not receive the protocol fixture: " + string.Join(" | ", lines));
                results.Add($"PASS {kind}: rsync / OpenSSH reached local proxy, sent SSH destination and banner, and received the fixture reply.");
            }
            File.WriteAllLines(report, results);
            return 0;
        }
        catch (Exception e) { results.Add(e.ToString()); File.WriteAllLines(report, results); return 1; }
    }
}

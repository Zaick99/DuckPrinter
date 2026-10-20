using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DuckPrinterManager.Core.Services;

public static class Network
{
    /// <summary>Ping ICMP; se il firewall lo blocca prova SMB/WinRM/RPC in TCP.</summary>
    public static async Task<bool> IsOnlineAsync(string host, int timeoutMs, bool tcpFallback, CancellationToken ct = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            if (reply.Status == IPStatus.Success) return true;
        }
        catch (PingException) { }
        catch (SocketException) { }

        if (!tcpFallback) return false;
        foreach (var port in new[] { 445, 5985, 135 })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(Math.Min(timeoutMs, 2000));
                await client.ConnectAsync(host, port, cts.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { }
        }
        return false;
    }

    /// <summary>
    /// IP / hostname / FQDN -> tutte le forme con cui puo' comparire su una porta di stampa
    /// (IP, nome breve, FQDN), cosi' la rimozione trova la coda comunque sia stata creata.
    /// </summary>
    public static async Task<List<string>> ExpandIdentityAsync(string target, int timeoutMs = 3000)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var t = target.Trim().TrimStart('\\').TrimEnd('.');
        if (t.Length == 0) return [];
        set.Add(t);
        if (!IPAddress.TryParse(t, out _) && t.Contains('.')) set.Add(t.Split('.')[0]);

        try
        {
            var lookup = Dns.GetHostEntryAsync(t);
            if (await Task.WhenAny(lookup, Task.Delay(timeoutMs)) == lookup && lookup.IsCompletedSuccessfully)
            {
                var e = lookup.Result;
                if (!string.IsNullOrEmpty(e.HostName))
                {
                    set.Add(e.HostName.TrimEnd('.'));
                    set.Add(e.HostName.Split('.')[0]);
                }
                foreach (var a in e.AddressList.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                    set.Add(a.ToString());
            }
        }
        catch { /* DNS non risolve: si usa quanto scritto */ }
        return set.ToList();
    }
}

using System;
using System.Net;
using System.Net.Sockets;

// Minimal Steam A2S_INFO query (UDP) to read a Rust/Source server's live player count. Pure BCL,
// no WPF dependency (kept in its own file, like UpdateParsing.cs, so scripts\test_a2s_parsing.ps1
// can compile and unit-test it). For Rust the query port equals the game port in "+connect host:port".
// Any timeout, network error or malformed reply returns false so the UI falls back to the static count.
public static class A2S
{
    // Query one server. Returns true and fills players/maxPlayers on a valid reply; false otherwise.
    public static bool TryQueryInfo(string host, int port, int timeoutMs, out int players, out int maxPlayers)
    {
        players = 0; maxPlayers = 0;
        try
        {
            using (var udp = new UdpClient())
            {
                udp.Client.ReceiveTimeout = timeoutMs;
                udp.Client.SendTimeout = timeoutMs;
                udp.Connect(host, port);

                byte[] req = BuildInfoRequest(null);
                udp.Send(req, req.Length);
                byte[] resp = Receive(udp);
                if (resp == null) return false;

                // Challenge (0x41 'A' after the 0xFFFFFFFF header): resend with the 4-byte challenge.
                if (resp.Length >= 9 && resp[4] == 0x41)
                {
                    var challenge = new byte[4];
                    Array.Copy(resp, 5, challenge, 0, 4);
                    req = BuildInfoRequest(challenge);
                    udp.Send(req, req.Length);
                    resp = Receive(udp);
                    if (resp == null) return false;
                }
                return ParseInfo(resp, out players, out maxPlayers);
            }
        }
        catch { return false; }
    }

    // FF FF FF FF 'T' "Source Engine Query\0" [challenge?]
    public static byte[] BuildInfoRequest(byte[] challenge)
    {
        byte[] payload = System.Text.Encoding.ASCII.GetBytes("Source Engine Query");
        var buf = new byte[4 + 1 + payload.Length + 1 + (challenge != null ? 4 : 0)];
        int i = 0;
        buf[i++] = 0xFF; buf[i++] = 0xFF; buf[i++] = 0xFF; buf[i++] = 0xFF;
        buf[i++] = 0x54; // 'T'
        Array.Copy(payload, 0, buf, i, payload.Length); i += payload.Length;
        buf[i++] = 0x00;
        if (challenge != null) { Array.Copy(challenge, 0, buf, i, 4); i += 4; }
        return buf;
    }

    static byte[] Receive(UdpClient udp)
    {
        try { var ep = new IPEndPoint(IPAddress.Any, 0); return udp.Receive(ref ep); }
        catch { return null; }
    }

    // Info reply (0x49 'I'): protocol byte, 4 null-terminated strings (Name, Map, Folder, Game),
    // short AppID, then the Players and MaxPlayers bytes we care about.
    public static bool ParseInfo(byte[] p, out int players, out int maxPlayers)
    {
        players = 0; maxPlayers = 0;
        if (p == null || p.Length < 6 || p[4] != 0x49) return false;
        int i = 6;                                   // skip 0xFFFFFFFF header, 'I', protocol byte
        for (int s = 0; s < 4; s++) if (!SkipString(p, ref i)) return false;  // Name, Map, Folder, Game
        i += 2;                                      // AppID (short)
        if (i + 1 >= p.Length) return false;
        players = p[i]; maxPlayers = p[i + 1];
        return true;
    }

    static bool SkipString(byte[] p, ref int i)
    {
        while (i < p.Length && p[i] != 0x00) i++;
        if (i >= p.Length) return false;
        i++;                                         // skip the null terminator
        return true;
    }
}

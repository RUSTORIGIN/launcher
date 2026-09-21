using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

// Minimal, dependency-free Discord Rich Presence over the local Discord IPC named pipe
// (\\.\pipe\discord-ipc-0 .. -9). Pure BCL - no NuGet, no native Discord SDK - so it fits the
// launcher's single-file csc build. Everything is best-effort: it silently no-ops when Discord
// is not running or no app id is set, reconnects if Discord starts later, and never throws to
// the caller. JSON payloads are hand-built (the messages are tiny).
public sealed class DiscordRpc
{
    readonly object _writeLock = new object();
    volatile bool _stop, _ready, _dirty;
    volatile NamedPipeClientStream _pipe;
    string _clientId;

    // current presence snapshot
    volatile string _details = "", _state = "", _largeImage = "", _largeText = "";
    long _startUnix;

    public void Start(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return;
        _clientId = clientId.Trim();
        _stop = false;
        new Thread(ManagerLoop) { IsBackground = true, Name = "discord-rpc" }.Start();
    }

    // Update what Discord shows. Thread-safe; the manager thread pushes it when connected.
    public void SetPresence(string details, string state, long startUnix, string largeImage, string largeText)
    {
        _details = details ?? ""; _state = state ?? ""; _startUnix = startUnix;
        _largeImage = largeImage ?? ""; _largeText = largeText ?? "";
        _dirty = true;
    }

    public void Stop()
    {
        _stop = true;
        Disconnect();
    }

    void ManagerLoop()
    {
        while (!_stop)
        {
            try
            {
                if (_pipe == null) Connect();
                if (_pipe != null && _ready && _dirty) { WriteFrame(1, BuildActivity()); _dirty = false; }
            }
            catch { Disconnect(); }
            Thread.Sleep(_pipe == null ? 15000 : 500);   // slow retry while Discord is absent
        }
        Disconnect();
    }

    void Connect()
    {
        for (int i = 0; i < 10 && !_stop; i++)
        {
            NamedPipeClientStream p = null;
            try
            {
                p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                p.Connect(500);
                _pipe = p; _ready = false;
                WriteFrame(0, "{\"v\":1,\"client_id\":\"" + Esc(_clientId) + "\"}");   // handshake
                new Thread(ReaderLoop) { IsBackground = true, Name = "discord-rpc-read" }.Start(p);
                return;
            }
            catch { try { if (p != null) p.Dispose(); } catch { } }
        }
        // none answered -> leave _pipe null and retry later
    }

    void Disconnect()
    {
        _ready = false;
        var p = _pipe; _pipe = null;
        try { if (p != null) p.Dispose(); } catch { }
    }

    void ReaderLoop(object state)
    {
        var p = (NamedPipeClientStream)state;
        try
        {
            while (!_stop && p != null && p.IsConnected)
            {
                int op; string json;
                if (!ReadFrame(p, out op, out json)) break;
                if (op == 3) { try { WriteFrame(4, json); } catch { } }                                   // PING -> PONG
                else if (op == 1 && json.IndexOf("\"READY\"", StringComparison.Ordinal) >= 0) { _ready = true; _dirty = true; }
                else if (op == 2) break;                                                                    // CLOSE
            }
        }
        catch { }
        if (_pipe == p) Disconnect();
    }

    string BuildActivity()
    {
        var sb = new StringBuilder();
        sb.Append("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":");
        sb.Append(System.Diagnostics.Process.GetCurrentProcess().Id);
        sb.Append(",\"activity\":{");
        bool any = false;
        if (_details.Length > 0) { sb.Append("\"details\":\"").Append(Esc(_details)).Append("\""); any = true; }
        if (_state.Length > 0) { if (any) sb.Append(','); sb.Append("\"state\":\"").Append(Esc(_state)).Append("\""); any = true; }
        if (_startUnix > 0) { if (any) sb.Append(','); sb.Append("\"timestamps\":{\"start\":").Append(_startUnix).Append('}'); any = true; }
        if (_largeImage.Length > 0)
        {
            if (any) sb.Append(',');
            sb.Append("\"assets\":{\"large_image\":\"").Append(Esc(_largeImage)).Append('"');
            if (_largeText.Length > 0) sb.Append(",\"large_text\":\"").Append(Esc(_largeText)).Append('"');
            sb.Append('}');
        }
        sb.Append("}},\"nonce\":\"").Append(Guid.NewGuid().ToString()).Append("\"}");
        return sb.ToString();
    }

    void WriteFrame(int op, string json)
    {
        var p = _pipe; if (p == null) return;
        byte[] data = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        WriteInt(header, 0, op); WriteInt(header, 4, data.Length);
        lock (_writeLock) { p.Write(header, 0, 8); p.Write(data, 0, data.Length); p.Flush(); }
    }

    static bool ReadFrame(Stream s, out int op, out string json)
    {
        op = 0; json = null;
        byte[] header = ReadExact(s, 8); if (header == null) return false;
        op = (int)ReadUInt(header, 0);
        int len = (int)ReadUInt(header, 4);
        if (len < 0 || len > (1 << 20)) return false;
        byte[] data = len == 0 ? new byte[0] : ReadExact(s, len);
        if (data == null) return false;
        json = Encoding.UTF8.GetString(data);
        return true;
    }

    static byte[] ReadExact(Stream s, int n)
    {
        var buf = new byte[n]; int off = 0;
        while (off < n) { int r = s.Read(buf, off, n - off); if (r <= 0) return null; off += r; }
        return buf;
    }

    static void WriteInt(byte[] b, int i, int v) { b[i] = (byte)v; b[i + 1] = (byte)(v >> 8); b[i + 2] = (byte)(v >> 16); b[i + 3] = (byte)(v >> 24); }
    static uint ReadUInt(byte[] b, int i) { return (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)); }

    static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}

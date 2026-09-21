using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Minimal, dependency-free Discord Rich Presence over the local Discord IPC named pipe
// (\\.\pipe\discord-ipc-0 .. -9). Pure BCL - no NuGet, no native Discord SDK - so it fits the
// launcher's single-file csc build. Everything is best-effort: it silently no-ops when Discord
// is not running or no app id is set, reconnects if Discord starts later, and never throws.
//
// Single-threaded on purpose: one background thread both reads and writes the same pipe, never
// concurrently (a concurrent blocking read on a second thread deadlocks the write on the same
// handle). Incoming frames are polled non-blockingly with PeekNamedPipe, so writes always happen
// while no read is in flight.
public sealed class DiscordRpc
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PeekNamedPipe(SafeHandle hNamedPipe, byte[] lpBuffer, uint nBufferSize,
        out uint lpBytesRead, out uint lpTotalBytesAvail, out uint lpBytesLeftThisMessage);

    volatile bool _stop, _ready, _dirty;
    NamedPipeClientStream _pipe;
    string _clientId;
    public Action<string> OnLog;
    void L(string m) { try { if (OnLog != null) OnLog(m); } catch { } }

    volatile string _details = "", _state = "", _largeImage = "", _largeText = "", _btnLabel = "", _btnUrl = "";
    long _startUnix;

    public void Start(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return;
        _clientId = clientId.Trim();
        _stop = false;
        new Thread(Loop) { IsBackground = true, Name = "discord-rpc" }.Start();
    }

    public void SetPresence(string details, string state, long startUnix, string largeImage, string largeText, string btnLabel, string btnUrl)
    {
        _details = details ?? ""; _state = state ?? ""; _startUnix = startUnix;
        _largeImage = largeImage ?? ""; _largeText = largeText ?? "";
        _btnLabel = btnLabel ?? ""; _btnUrl = btnUrl ?? "";
        _dirty = true;
    }

    public void Stop() { _stop = true; Disconnect(); }

    void Loop()
    {
        while (!_stop)
        {
            try
            {
                if (_pipe == null) { if (!Connect()) { Thread.Sleep(15000); continue; } }
                if (_ready && _dirty) { WriteFrame(1, BuildActivity()); _dirty = false; L("activity sent"); }

                if (Available() >= 8)
                {
                    int op; string json;
                    if (!ReadFrame(_pipe, out op, out json)) { Disconnect(); continue; }
                    if (op == 1 && json.IndexOf("\"READY\"", StringComparison.Ordinal) >= 0) { _ready = true; _dirty = true; L("ready"); }
                    else if (op == 3) WriteFrame(4, json);                 // PING -> PONG
                    else if (op == 2) { L("closed: " + json); Disconnect(); }
                }
                else Thread.Sleep(250);
            }
            catch (Exception ex) { L("error: " + ex.GetType().Name + ": " + ex.Message); Disconnect(); Thread.Sleep(3000); }
        }
        Disconnect();
    }

    bool Connect()
    {
        for (int i = 0; i < 10 && !_stop; i++)
        {
            NamedPipeClientStream p = null;
            try
            {
                p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut);
                p.Connect(500);
                _pipe = p; _ready = false;
                WriteFrame(0, "{\"v\":1,\"client_id\":\"" + Esc(_clientId) + "\"}");   // handshake
                L("connected on discord-ipc-" + i);
                return true;
            }
            catch { try { if (p != null) p.Dispose(); } catch { } }
        }
        return false;
    }

    void Disconnect()
    {
        _ready = false;
        var p = _pipe; _pipe = null;
        try { if (p != null) p.Dispose(); } catch { }
    }

    int Available()
    {
        var p = _pipe; if (p == null) return 0;
        try { uint r, a, m; if (PeekNamedPipe(p.SafePipeHandle, null, 0, out r, out a, out m)) return (int)a; }
        catch { }
        return 0;
    }

    string BuildActivity()
    {
        var sb = new StringBuilder();
        sb.Append("{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":");
        sb.Append(System.Diagnostics.Process.GetCurrentProcess().Id);
        sb.Append(",\"activity\":{");
        bool any = false;
        if (_details.Length > 0) { sb.Append("\"details\":\"").Append(Esc(_details)).Append('"'); any = true; }
        if (_state.Length > 0) { if (any) sb.Append(','); sb.Append("\"state\":\"").Append(Esc(_state)).Append('"'); any = true; }
        if (_startUnix > 0) { if (any) sb.Append(','); sb.Append("\"timestamps\":{\"start\":").Append(_startUnix).Append('}'); any = true; }
        if (_largeImage.Length > 0)
        {
            if (any) sb.Append(',');
            sb.Append("\"assets\":{\"large_image\":\"").Append(Esc(_largeImage)).Append('"');
            if (_largeText.Length > 0) sb.Append(",\"large_text\":\"").Append(Esc(_largeText)).Append('"');
            sb.Append('}');
        }
        if (_btnUrl.Length > 0)
        {
            if (any) sb.Append(',');
            string label = _btnLabel.Length > 0 ? _btnLabel : _btnUrl;
            sb.Append("\"buttons\":[{\"label\":\"").Append(Esc(label)).Append("\",\"url\":\"").Append(Esc(_btnUrl)).Append("\"}]");
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
        p.Write(header, 0, 8); p.Write(data, 0, data.Length); p.Flush();
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

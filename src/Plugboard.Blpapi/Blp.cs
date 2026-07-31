using Bloomberglp.Blpapi;
using BbgMessage = Bloomberglp.Blpapi.Message;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Plugboard.Blpapi;

// The shared BLPAPI core. ONE managed BLPAPI wrapper and ONE Session for the whole
// process, used by every BLPAPI connector (bloomberg, cmp, ...). BLPAPI's native
// library (blpapi3_64) is a process-global singleton, so a second independent managed
// instance in another load context cannot get a working session ("Session Not Started").
// Loading this assembly once in the host's default context - and letting plugins share it
// - is what makes several BLPAPI connectors coexist.
//
// Services (//blp/refdata, //blp/cmp, //blp/apiflds, ...) are opened lazily on the single
// session and cached; that is how CMP stays a distinct connector while sharing the session.
public static class Blp
{
    public const string Host = "localhost";
    public const int    Port = 8194;

    private static Session? _session;
    private static readonly ConcurrentDictionary<string, Service> _services = new();
    private static readonly object _initLock = new();
    private static volatile bool _sessionReady;
    private static volatile string? _lastError;

    private static void Log(string msg) => Console.WriteLine("[blpapi] " + msg);

    private static (Session session, Service svc) GetService(string serviceName)
    {
        lock (_initLock)
        {
            if (_session == null)
            {
                var opts = new SessionOptions
                {
                    ServerHost = Host,
                    ServerPort = Port,
                    AuthenticationOptions = "AuthenticationType=OS_LOGON"
                };
                var s = new Session(opts);
                if (!s.Start()) throw new Exception("Bloomberg session failed to start");
                _session = s;
                EnsureEventThread(_session);
            }
            if (!_services.TryGetValue(serviceName, out var cached))
            {
                if (!_session.OpenService(serviceName)) throw new Exception($"Could not open {serviceName}");
                cached = _session.GetService(serviceName);
                _services[serviceName] = cached;
            }
            return (_session, cached);
        }
    }

    private static void ResetSession()
    {
        lock (_initLock)
        {
            var dead = _session;
            _session = null; _services.Clear();
            _eventThread = null; _sessionReady = false;
            if (dead != null)
                try { Task.Run(() => { try { dead.Stop(); } catch { } }); } catch { }
        }
    }

    // Run a request against a service, transparently recovering from a dead session
    // (reset + one retry). The single point every connector goes through.
    public static T WithService<T>(string serviceName, Func<Session, Service, T> work)
    {
        try
        {
            var (session, svc) = GetService(serviceName);
            var r = work(session, svc);
            _sessionReady = true; _lastError = null;
            return r;
        }
        catch (Exception first)
        {
            Log($"request failed ({first.Message}); resetting session and retrying once");
            _lastError = first.Message;
            ResetSession();
            var (session, svc) = GetService(serviceName);
            var r = work(session, svc);
            _sessionReady = true; _lastError = null;
            return r;
        }
    }

    private static readonly ConcurrentDictionary<long, BlockingCollection<BbgMessage>> _pending = new();
    private static Thread? _eventThread;

    private static void EnsureEventThread(Session session)
    {
        if (_eventThread != null && _eventThread.IsAlive) return;
        _eventThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var evt = session.NextEvent(60000);
                    bool isFinal = evt.Type == Event.EventType.RESPONSE;
                    if (evt.Type == Event.EventType.SESSION_STATUS)
                    {
                        foreach (BbgMessage m in evt)
                        {
                            var mt = m.MessageType.ToString();
                            if (mt is "SessionTerminated" or "SessionConnectionDown" or "SessionStartupFailure")
                            {
                                Log($"session status {mt} - resetting session");
                                ResetSession();
                                return;
                            }
                        }
                        continue;
                    }
                    if (evt.Type == Event.EventType.SERVICE_STATUS ||
                        evt.Type == Event.EventType.TIMEOUT) continue;
                    foreach (BbgMessage msg in evt)
                    {
                        long key = msg.CorrelationID.Value;
                        if (!_pending.TryGetValue(key, out var q)) continue;
                        q.Add(msg);
                        if (isFinal) q.CompleteAdding();
                    }
                }
                catch (Exception ex) { Log($"event thread error: {ex.Message}"); break; }
            }
        }) { IsBackground = true, Name = "BlpapiEventDispatch" };
        _eventThread.Start();
    }

    public static List<BbgMessage> SendAndReceive(Session session, Request req, int timeoutMs = 30000)
    {
        var q   = new BlockingCollection<BbgMessage>();
        var cid = new CorrelationID();
        _pending[cid.Value] = q;
        session.SendRequest(req, cid);
        var messages = new List<BbgMessage>();
        try
        {
            while (!q.IsCompleted)
            {
                if (q.TryTake(out BbgMessage? msg, timeoutMs)) messages.Add(msg);
                else break;
            }
            while (q.TryTake(out BbgMessage? msg2)) messages.Add(msg2);
        }
        finally { _pending.TryRemove(cid.Value, out _); if (!q.IsAddingCompleted) q.CompleteAdding(); }
        return messages;
    }

    // Status probe for a service: is the Terminal reachable, and does the service open
    // (i.e. is the session entitled for it)? Used by connector /status endpoints.
    public sealed record Probe(bool Connected, bool ServiceReady, bool LastKnownReady, string? Error);

    public static Probe ProbeService(string serviceName)
    {
        bool connected = false;
        try { using var tcp = new System.Net.Sockets.TcpClient(); tcp.Connect(Host, Port); connected = tcp.Connected; }
        catch { }

        bool ready = false; string? error = null;
        try { GetService(serviceName); ready = true; _sessionReady = true; _lastError = null; }
        catch (Exception ex) { error = ex.Message; _lastError = ex.Message; ResetSession(); }
        return new Probe(connected, ready, _sessionReady, error ?? _lastError);
    }

    // ── date-override normalization (BLPAPI wants YYYYMMDD) ──
    private static readonly System.Text.RegularExpressions.Regex _ymd = new(@"^(\d{4})[/-](\d{1,2})[/-](\d{1,2})$");
    private static readonly System.Text.RegularExpressions.Regex _mdy = new(@"^(\d{1,2})[/-](\d{1,2})[/-](\d{4})$");

    public static string NormalizeOverrideValue(JsonElement v)
    {
        var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText();
        if (string.IsNullOrWhiteSpace(s)) return s;
        var t = s.Trim();
        var m = _ymd.Match(t);
        if (m.Success) return m.Groups[1].Value + m.Groups[2].Value.PadLeft(2, '0') + m.Groups[3].Value.PadLeft(2, '0');
        m = _mdy.Match(t);
        if (m.Success) return m.Groups[3].Value + m.Groups[1].Value.PadLeft(2, '0') + m.Groups[2].Value.PadLeft(2, '0');
        return s;
    }

    // ── JSON <-> BLPAPI Element (typed request build + response serialize) ──

    public static object? ElementToJson(Element e)
    {
        if (e.IsNull) return null;
        if (e.IsArray)
        {
            var list = new List<object?>(e.NumValues);
            bool complex = e.Datatype == Schema.Datatype.SEQUENCE || e.Datatype == Schema.Datatype.CHOICE;
            for (int i = 0; i < e.NumValues; i++)
                list.Add(complex ? ElementToJson(e.GetValueAsElement(i)) : ScalarValue(e, i));
            return list;
        }
        if (e.IsComplexType)
        {
            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < e.NumElements; i++)
            {
                var c = e.GetElement(i);
                dict[c.Name.ToString()] = ElementToJson(c);
            }
            return dict;
        }
        return ScalarValue(e, -1);
    }

    private static object? ScalarValue(Element e, int idx)
    {
        try
        {
            switch (e.Datatype)
            {
                case Schema.Datatype.BOOL:
                    return idx < 0 ? e.GetValueAsBool() : e.GetValueAsBool(idx);
                case Schema.Datatype.INT32:
                case Schema.Datatype.INT64:
                    return idx < 0 ? e.GetValueAsInt64() : e.GetValueAsInt64(idx);
                case Schema.Datatype.FLOAT32:
                case Schema.Datatype.FLOAT64:
                case Schema.Datatype.DECIMAL:
                    return idx < 0 ? e.GetValueAsFloat64() : e.GetValueAsFloat64(idx);
                default:
                    return idx < 0 ? e.GetValueAsString() : e.GetValueAsString(idx);
            }
        }
        catch { try { return idx < 0 ? e.GetValueAsString() : e.GetValueAsString(idx); } catch { return null; } }
    }

    public static void ApplyJsonToElement(Element el, JsonElement json)
    {
        foreach (var prop in json.EnumerateObject())
        {
            var name = prop.Name;
            var val = prop.Value;
            switch (val.ValueKind)
            {
                case JsonValueKind.Array:
                    var arr = el.GetElement(name);
                    foreach (var item in val.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object) ApplyJsonToElement(arr.AppendElement(), item);
                        else AppendScalar(arr, item);
                    }
                    break;
                case JsonValueKind.Object:
                    ApplyJsonToElement(el.GetElement(name), val);
                    break;
                default:
                    var tgt = el.GetElement(name);
                    if (tgt.IsArray) AppendScalar(tgt, val);
                    else SetScalar(el, name, val);
                    break;
            }
        }
    }

    private static void SetScalar(Element el, string name, JsonElement v)
    {
        try
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.True:  el.SetElement(name, true);  return;
                case JsonValueKind.False: el.SetElement(name, false); return;
                case JsonValueKind.Number:
                    if (v.TryGetInt32(out var i)) { el.SetElement(name, i); return; }
                    if (v.TryGetInt64(out var l)) { el.SetElement(name, l); return; }
                    el.SetElement(name, v.GetDouble()); return;
                default:
                    el.SetElement(name, v.GetString() ?? ""); return;
            }
        }
        catch { el.SetElement(name, v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()); }
    }

    private static void AppendScalar(Element arr, JsonElement v)
    {
        try
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.True:  arr.AppendValue(true);  return;
                case JsonValueKind.False: arr.AppendValue(false); return;
                case JsonValueKind.Number:
                    if (v.TryGetInt64(out var l)) { arr.AppendValue(l); return; }
                    arr.AppendValue(v.GetDouble()); return;
                default:
                    arr.AppendValue(v.GetString() ?? ""); return;
            }
        }
        catch { arr.AppendValue(v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()); }
    }
}

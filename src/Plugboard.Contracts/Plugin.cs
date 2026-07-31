namespace Plugboard.Contracts;

// A capability module. The host discovers DLLs in the plugins directory,
// verifies each one's signature, loads it, and calls Register() so the plugin
// can attach its routes. Implement this in a plugin DLL.
public interface IPlugin
{
    string Name { get; }
    void Register(IEndpointRegistry registry);
}

// Plugins attach handlers here. The host exposes each at /con/<route> (connector)
// or /svc/<route> (service) — a route registered as "svc/..." mounts under /svc/,
// anything else under /con/. Units address each other by BARE name (no prefix).
// Optional RouteInfo feeds the host's self-describing /catalog endpoint (clients
// read the live API from the gateway instead of bundling a static snapshot).
public interface IEndpointRegistry
{
    void Map(string method, string route, Func<PluginRequest, Task<object?>> handler, RouteInfo? info = null);
}

// Optional, human/tooling-facing metadata for a route. All fields optional;
// Sample is an example request body. Params describes the input parameters so the
// host can project them into JSON Schema for AI tool-calling (see /tools) and show
// them in the catalog/console. A route with no Params still works; it just appears
// as "no declared inputs" so an agent knows it does not know, rather than guessing.
public sealed record RouteInfo(string? Summary = null, string? Description = null,
    object? Sample = null, ParamInfo[]? Params = null);

// One input parameter of a route. Deliberately simpler than raw JSON Schema so
// authors can declare it inline; the host projects it into JSON Schema. Type is a
// JSON type: string | number | integer | boolean | array | object. Items is the
// element type when Type is "array". Enum constrains allowed values.
public sealed record ParamInfo(
    string Name,
    string Type = "string",
    bool Required = false,
    string? Description = null,
    string? Items = null,
    string[]? Enum = null,
    object? Default = null);

// What a handler receives. The host wraps whatever the handler returns in
// { ok: true, data } and any thrown exception in { ok: false, error }.
public sealed class PluginRequest
{
    public required string Method { get; init; }
    public required string Route { get; init; }
    public required string Body { get; init; }                      // raw request body (JSON)
    public required IReadOnlyDictionary<string, string> Query { get; init; }

    // The composition mesh: call another registered unit by name, in-process.
    // A service (a unit whose handler calls others) uses this to compose
    // connectors and other services, e.g.
    //   var q = await req.Call("bloomberg/bdp", new { securities, fields });
    // Payload is serialized to the target's Body (a string passes through as-is);
    // the return value is whatever that unit's handler returned. The host injects
    // the real implementation per request; the default is a no-op so a plugin
    // tested in isolation still constructs. Depth guards against call cycles.
    public Func<string, object?, Task<object?>> Call { get; init; } = (_, _) => Task.FromResult<object?>(null);
    public int Depth { get; init; }
}

# 10 - Plugin SDK (write a capability)

A **plugin** adds capabilities to the Plugboard host. The host discovers DLLs in its
`plugins/` directory, verifies each one's signature (managed posture only), loads it into
its own dependency context, and calls `Register()` so the plugin can attach HTTP routes.
The whole surface you code against is one assembly: **`Plugboard.Contracts`**.

## The contract (Plugboard.Contracts)

```csharp
public interface IPlugin
{
    string Name { get; }
    void Register(IEndpointRegistry registry);
}

public interface IEndpointRegistry
{
    void Map(string method, string route,
             Func<PluginRequest, Task<object?>> handler,
             RouteInfo? info = null);
}

public sealed class PluginRequest
{
    public string Method { get; init; }                       // "GET" | "POST" | ...
    public string Route  { get; init; }                       // the route as registered
    public string Body   { get; init; }                       // raw request body (JSON string)
    public IReadOnlyDictionary<string,string> Query { get; init; }
    public Func<string, object?, Task<object?>> Call { get; init; }   // the in-process mesh
}
```

- **Route prefixing:** a route registered as `"svc/..."` mounts under `/svc/`; anything
  else mounts under `/con/`. So `Map("GET","ping/hello",...)` serves `GET /con/ping/hello`.
- **Return value:** whatever your handler returns is wrapped by the host as
  `{ "ok": true, "data": <your value> }`; a thrown exception becomes
  `{ "ok": false, "error": "..." }`. Return plain objects/records - don't wrap them yourself.
- **Units address each other by bare name** (no `/con` or `/svc` prefix) via `req.Call`.

## Minimal plugin

```csharp
using Plugboard.Contracts;

namespace Acme.Plugins.Hello;

public sealed class HelloPlugin : IPlugin
{
    public string Name => "hello";

    public void Register(IEndpointRegistry registry)
    {
        // GET /con/hello/ping
        registry.Map("GET", "hello/ping", _ =>
            Task.FromResult<object?>(new { message = "pong", at = DateTime.UtcNow.ToString("o") }));

        // POST /con/hello/echo
        registry.Map("POST", "hello/echo", req =>
            Task.FromResult<object?>(new { youSent = req.Body, query = req.Query }));
    }
}
```

## The project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <!-- The host SHARES its own Plugboard.Contracts at runtime. Reference it but do
         NOT copy it into the plugin output, or IPlugin type identity won't match and
         the host will refuse to load the plugin. -->
    <ProjectReference Include="..\..\Plugboard.Contracts\Plugboard.Contracts.csproj">
      <Private>false</Private>
      <ExcludeAssets>runtime</ExcludeAssets>
    </ProjectReference>
    <!-- Or, if consuming the published SDK package instead of the repo: -->
    <!-- <PackageReference Include="Plugboard.Contracts" Version="1.0.0" ExcludeAssets="runtime" /> -->
  </ItemGroup>
</Project>
```

> **The one rule that bites everyone:** never ship your own copy of `Plugboard.Contracts.dll`
> in the plugin folder. The host loads one canonical Contracts; a duplicate breaks the
> `IPlugin` type identity and the plugin silently fails to register. Hence
> `Private=false` + `ExcludeAssets=runtime`.

## Self-describing routes (feeds /catalog and AI /tools)

`RouteInfo` / `ParamInfo` are optional but recommended: they let the host project your
route into the `/catalog` (human/console) and `/tools` (JSON-Schema for AI tool-calling)
surfaces, so clients read the live API instead of bundling a stale snapshot.

```csharp
registry.Map("POST", "hello/greet", async req => { /* ... */ },
    new RouteInfo(
        Summary: "Greet someone",
        Description: "Returns a greeting for the given name.",
        Sample: new { name = "Ada" },
        Params: new[] { new ParamInfo("name", "string", Required: true, Description: "Who to greet.") }));
```

`ParamInfo` types are JSON types: `string | number | integer | boolean | array | object`
(`Items` gives the element type for arrays; `Enum` constrains values).

## Services: compose other units with req.Call

A **service** owns no external resource; it composes connectors/other services in-process.
Register under `svc/` and use `req.Call("<bare-name>", payload)`:

```csharp
public sealed class PricesPlugin : IPlugin
{
    public string Name => "prices";
    public void Register(IEndpointRegistry r) =>
        r.Map("POST", "svc/prices", async req =>
        {
            var quotes = await req.Call("bloomberg/bdp",
                new { securities = new[] { "AAPL US Equity" }, fields = new[] { "PX_LAST", "NAME" } });
            return (object?)new { asOf = DateTime.UtcNow.ToString("o"), quotes };
        });
}
```

`Call` serializes the payload to the target's `Body` (a string passes through as-is) and
returns whatever that unit's handler returned. Cycles are depth-guarded.

## Optional plugin.json (metadata)

Drop a `plugin.json` beside the DLL for catalog/console display:

```json
{
  "id": "hello",
  "version": "1.0.0",
  "displayName": "Hello",
  "description": "Demo capability.",
  "requires": []
}
```

## Build and deploy

```powershell
# builds every src/plugins/* and deploys each into the host's plugins/ dir
.\tools\build-plugins.ps1
# then run the host; it loads plugins on startup
dotnet build src\Plugboard.Host\Plugboard.Host.csproj -c Release
.\src\Plugboard.Host\bin\Release\net8.0-windows\Plugboard.Host.exe
```

Each plugin deploys into `plugins/<Name>/` with its **full dependency closure**
(`deps.json` + dependency DLLs + native bits), minus `Plugboard.Contracts.dll` (shared).

## Lite vs managed posture (signing)

Set in `src/Plugboard.Host/appsettings.json`:

```json
{ "RequireSignature": false, "TrustedKeys": [] }
```

- **Lite** (`RequireSignature: false`): unsigned DLLs load. Fine for local dev.
- **Managed** (`RequireSignature: true`): every plugin DLL needs a `<dll>.sig` from a
  trusted key. Generate keys and sign:

```powershell
.\tools\gen-keys.ps1                                        # -> plugboard-private/public.key
.\tools\build-plugins.ps1 -Key plugboard-private.key        # build + sign in one step
# or sign an existing DLL:
.\tools\sign-plugin.ps1 -Dll path\to\MyPlugin.dll -Key plugboard-private.key
```

Add the public key to `TrustedKeys` in `appsettings.json`. Private keys are git-ignored.

## Lifecycle recap

discover DLL in `plugins/` -> (managed) verify `.sig` against a trusted key -> load in an
isolated context -> `Register(registry)` -> routes served at `/con/<route>` or `/svc/<route>`
-> visible in `/catalog` and `/tools`.

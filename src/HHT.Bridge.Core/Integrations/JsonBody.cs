using System.Net.Http.Headers;
using System.Text.Json;

namespace HHT.Bridge.Core.Integrations;

/// <summary>
/// JSON request body with an explicit Content-Length (no chunked transfer encoding), so it works with every
/// server and proxy (Python's http.server, older reverse proxies, Caddy, uvicorn).
/// </summary>
public static class JsonBody
{
    public static ByteArrayContent Create(object value, JsonSerializerOptions? options = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }
}

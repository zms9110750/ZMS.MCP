using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

Dictionary<string, JsonObject> _toolSchemas = [];

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters => {
        filters.AddCallToolFilter(next => {
            return async (request, ct) => {
                try { return await next(request, ct); }
                catch (Exception ex)
                {
                    return new CallToolResult {
                        Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }],
                        IsError = true
                    };
                }
            };
        });
    })
    .WithMessageFilters(filters => {
        filters.AddIncomingFilter(next => (context, ct) => {
            var msg = context.JsonRpcMessage;

            if (msg is JsonRpcResponse resp &&
                resp.Result is JsonObject result &&
                result.TryGetPropertyValue("tools", out var toolsToken) &&
                toolsToken is JsonArray tools)
            {
                _toolSchemas.Clear();
                foreach (var tool in tools)
                {
                    if (tool is JsonObject t &&
                        t.TryGetPropertyValue("name", out var nameToken) &&
                        t.TryGetPropertyValue("inputSchema", out var schemaToken) &&
                        schemaToken is JsonObject schema)
                    {
                        _toolSchemas[nameToken!.GetValue<string>()] = schema;
                    }
                }
            }

            if (msg is JsonRpcRequest req &&
                req.Method == "tools/call" &&
                req.Params is JsonObject callParams &&
                callParams.TryGetPropertyValue("arguments", out var argsToken) &&
                argsToken is JsonObject args &&
                callParams.TryGetPropertyValue("name", out var toolNameToken))
            {
                var toolName = toolNameToken!.GetValue<string>();
                if (_toolSchemas.TryGetValue(toolName, out var schema))
                {
                    FillMissingProperties(args, schema);
                }
            }

            return next(context, ct);
        });
    });

await builder.Build().RunAsync();

static void FillMissingProperties(JsonObject args, JsonObject schema)
{
    var required = new HashSet<string>();
    if (schema.TryGetPropertyValue("required", out var reqToken) && reqToken is JsonArray reqArr)
    {
        foreach (var item in reqArr)
        {
            if (item is JsonValue v)
            {
                required.Add((string)v!);
            }
        }
    }

    if (schema.TryGetPropertyValue("properties", out var propsToken) && propsToken is JsonObject props)
    {
        foreach (var kv in props)
        {
            if (!required.Contains(kv.Key) && !args.ContainsKey(kv.Key))
            {
                args[kv.Key] = null;
            }
        }
    }
}

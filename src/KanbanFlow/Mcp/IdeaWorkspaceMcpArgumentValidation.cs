using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KanbanFlow.Mcp;

/// <summary>Rejects unsupported workspace tool arguments before the SDK binds method parameters.</summary>
public static class IdeaWorkspaceMcpArgumentValidation
{
    private static readonly HashSet<string> WorkspaceToolNames = typeof(IdeaWorkspaceMcpTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> RejectUnknownArguments(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next)
        => (request, cancellationToken) =>
        {
            if (request.Params is { } parameters && WorkspaceToolNames.Contains(parameters.Name))
            {
                var tool = request.MatchedPrimitive as McpServerTool;
                if (tool is null)
                    request.Server.ServerOptions.ToolCollection?.TryGetPrimitive(parameters.Name, out tool);

                if (tool is null)
                    throw new McpException("invalid_arguments (400): The workspace tool's input schema is unavailable.");

                ValidateArguments(tool.ProtocolTool.InputSchema, parameters.Arguments);
            }

            return next(request, cancellationToken);
        };

    private static void ValidateArguments(JsonElement schema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        var names = arguments?.Select(argument => argument.Key).ToHashSet(StringComparer.Ordinal)
            ?? [];
        var hasProperties = schema.TryGetProperty("properties", out var properties);
        foreach (var name in names)
        {
            if (!hasProperties || !properties.TryGetProperty(name, out _))
                throw new McpException($"invalid_arguments (400): Unsupported argument '{name}'. Use the tool's advertised input fields.");
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var property in required.EnumerateArray())
            {
                var name = property.GetString()!;
                if (!names.Contains(name))
                    throw new McpException($"invalid_arguments (400): Missing required argument '{name}'.");
            }
        }
    }
}

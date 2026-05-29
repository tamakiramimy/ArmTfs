using System.Text.Json;
using System.Text.Json.Serialization;
using ArmTfs.Core.Models;
using Microsoft.VisualStudio.Services.WebApi;

namespace ArmTfs.Cli.Commands;

internal static class JsonOutput
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Write(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, Options));
    }

    public static object Workspace(WorkspaceMetadata metadata) => new
    {
        name = metadata.Name,
        owner = metadata.Owner,
        serverCollectionUrl = metadata.ServerCollectionUrl,
        mappings = metadata.Mappings.Select(m => new
        {
            serverPath = m.ServerPath,
            localPath = m.LocalPath,
        })
    };

    public static object? Identity(IdentityRef? identity) => identity is null
        ? null
        : new
        {
            displayName = identity.DisplayName,
            uniqueName = identity.UniqueName,
        };

    public static string EnumValue(Enum value)
    {
        var raw = value.ToString();
        return string.IsNullOrEmpty(raw)
            ? string.Empty
            : char.ToLowerInvariant(raw[0]) + raw[1..];
    }
}
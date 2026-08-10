using System.Reflection;

namespace VirtualTickets.Api.Services;

public static class BuildInformation
{
    public static string Sha { get; } = ResolveSha();

    private static string ResolveSha()
    {
        var value = typeof(BuildInformation).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildSha")?.Value;

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}

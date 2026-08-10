using System.Reflection;
using VirtualTickets.Api.Services;
using VirtualTickets.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Xunit;

namespace VirtualTickets.Api.Tests;

public sealed class BuildInformationTests
{
    [Fact]
    public void Build_sha_comes_from_compiled_assembly_metadata()
    {
        var metadata = typeof(BuildInformation).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "BuildSha");

        Assert.Equal(metadata.Value, BuildInformation.Sha);
        Assert.False(string.IsNullOrWhiteSpace(BuildInformation.Sha));
    }

    [Fact]
    public void Health_response_exposes_the_compiled_build_sha()
    {
        var result = Assert.IsType<OkObjectResult>(new HealthController().Get());
        var json = JsonSerializer.Serialize(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"status\":\"healthy\"", json);
        Assert.Contains($"\"buildSha\":\"{BuildInformation.Sha}\"", json);
    }

    [Fact]
    public void Docker_and_ci_pass_the_same_sha_to_binary_label_and_image_tag()
    {
        var root = ProjectRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "dotnet-ci.yml"));

        Assert.Contains("/p:BuildSha=$BUILD_SHA", dockerfile);
        Assert.Contains("org.opencontainers.image.revision=$BUILD_SHA", dockerfile);
        Assert.Contains("repository }}:${{ github.sha }}", workflow);
        Assert.Contains("BUILD_SHA=${{ github.sha }}", workflow);
    }

    private static string ProjectRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
}

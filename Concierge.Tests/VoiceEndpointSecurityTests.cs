using Concierge.Web.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Concierge.Tests;

public sealed class VoiceEndpointSecurityTests
{
    [Theory]
    [InlineData("C:\\private\\meeting.webm", "meeting.webm")]
    [InlineData("../../private/recording.wav", "recording.wav")]
    [InlineData(null, "capture.webm")]
    public void Uploaded_file_names_cannot_carry_directory_paths(string? supplied, string expected)
        => Assert.Equal(expected, VoiceEndpoints.SafeFileName(supplied));

    [Fact]
    public async Task Voice_routes_limit_request_bodies_before_model_binding()
    {
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.MapConciergeVoice();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToDictionary(endpoint => endpoint.RoutePattern.RawText!);

        Assert.Equal(6 * 1024 * 1024,
            routes["/api/voice/transcribe"].Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
        Assert.Equal(32 * 1024,
            routes["/api/voice/speak"].Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }
}

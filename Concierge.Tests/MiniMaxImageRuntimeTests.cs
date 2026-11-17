using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Media.Cloud;
using Concierge.Shared.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace Concierge.Tests;

public sealed class MiniMaxImageRuntimeTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    [Fact]
    public async Task Creates_a_picture_with_the_official_image_request()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("/v1/images/generations", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("MiniMax-H3-Image", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("South African jazz poster", body.RootElement.GetProperty("prompt").GetString());
            Assert.Equal("1024x1024", body.RootElement.GetProperty("size").GetString());
            return Response();
        }));
        var runtime = new MiniMaxImageRuntime(http, new MiniMaxImageOptions { ApiKey = "test-key" }, NullLogger<MiniMaxImageRuntime>.Instance);

        var image = Assert.Single(await runtime.GenerateAsync(new ImageGenerationRequest("South African jazz poster")));
        Assert.Equal(Png, image.Bytes);
        Assert.Equal("image/png", image.MimeType);
    }

    [Fact]
    public async Task Edits_with_reference_images_in_their_selected_order()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("/v1/images/edits", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("Put the blue hat from Picture 2 on Picture 1", body.RootElement.GetProperty("prompt").GetString());
            var images = body.RootElement.GetProperty("images").EnumerateArray().ToArray();
            Assert.Equal(2, images.Length);
            Assert.Equal("data:image/png;base64,AQID", images[0].GetProperty("image_url").GetString());
            Assert.Equal("data:image/jpeg;base64,BAUG", images[1].GetProperty("image_url").GetString());
            return Response();
        }));
        var runtime = new MiniMaxImageRuntime(http, new MiniMaxImageOptions { ApiKey = "test-key" }, NullLogger<MiniMaxImageRuntime>.Instance);

        var image = Assert.Single(await runtime.EditAsync(new ImageEditRequest(
            "Put the blue hat from Picture 2 on Picture 1",
            [new ImageReference("image/png", [1, 2, 3]), new ImageReference("image/jpeg", [4, 5, 6])])));
        Assert.Equal(Png, image.Bytes);
    }

    [Fact]
    public async Task Missing_key_never_sends_a_request()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromException<HttpResponseMessage>(
            new InvalidOperationException("Unexpected request"))));
        var runtime = new MiniMaxImageRuntime(http, new MiniMaxImageOptions(), NullLogger<MiniMaxImageRuntime>.Instance);
        Assert.False(runtime.IsReady);
        Assert.Empty(await runtime.GenerateAsync(new ImageGenerationRequest("a poster")));
        Assert.Empty(await runtime.EditAsync(new ImageEditRequest("change it", [new ImageReference("image/png", [1])])));
    }

    [Fact]
    public async Task Per_image_error_is_not_reported_as_a_success()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"error\":\"generation rejected\"}]}", Encoding.UTF8, "application/json"),
        }));
        var runtime = new MiniMaxImageRuntime(http, new MiniMaxImageOptions { ApiKey = "test-key" }, NullLogger<MiniMaxImageRuntime>.Instance);
        Assert.Empty(await runtime.GenerateAsync(new ImageGenerationRequest("a poster")));
    }

    private static HttpResponseMessage Response() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"data\":[{\"b64_json\":\"iVBORw0KGgo=\"}]}", Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : this(request => Task.FromResult(answer(request))) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => answer(request);
    }
}

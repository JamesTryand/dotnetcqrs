using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace DotnetCqrs.Tests.Crypto;

/// <summary>A real HTTP endpoint speaking the key-management facade's contract, for tests
/// that run a generated host as a separate process and point its <c>KMS_FACADE_URL</c>
/// here. Every request is forwarded to a <see cref="FakeKmsHandler"/>, so the contract fake
/// used by the unit tests is the one exercised over the wire too.</summary>
internal sealed class StandInKmsFacade : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpMessageInvoker _invoker;
    // FakeKmsHandler keeps plain collections, and a host flushes subjects concurrently.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FakeKmsHandler Handler { get; } = new();
    public string BaseUrl { get; }

    private StandInKmsFacade(int port)
    {
        BaseUrl = $"http://127.0.0.1:{port}/";
        _invoker = new HttpMessageInvoker(Handler);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(BaseUrl);
        _app = builder.Build();
        _app.Run(ForwardAsync);
    }

    public static async Task<StandInKmsFacade> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var facade = new StandInKmsFacade(port);
        await facade._app.StartAsync();
        return facade;
    }

    private async Task ForwardAsync(HttpContext context)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            new Uri(new Uri("https://kms.test/"), context.Request.Path.Value!.TrimStart('/')));
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);
        if (body.Length > 0) request.Content = new ByteArrayContent(body.ToArray());

        HttpResponseMessage response;
        await _gate.WaitAsync();
        try { response = await _invoker.SendAsync(request, context.RequestAborted); }
        finally { _gate.Release(); }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length == 0) return;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await context.Response.Body.WriteAsync(bytes);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _invoker.Dispose();
    }
}

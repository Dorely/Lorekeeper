using System.Text.Json;
using ElectronNET.API;
using Lorekeeper.Printing;

namespace Lorekeeper.Desktop;

public sealed class ElectronPrintHost(IConfiguration configuration) : IPrintHost
{
    private const string PrintHook = "lorekeeper-print";
    private const string CancelHook = "lorekeeper-print-cancel";
    private readonly Uri _baseUri = CreateBaseUri(configuration);

    public bool IsDesktop => HybridSupport.IsElectronActive;

    public async Task PrintAsync(PrintPreparedJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsDesktop)
            throw new InvalidOperationException("System printing is available only in the desktop application.");

        var uri = ResolveDocumentUri(job.DocumentUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var call = Electron.HostHook.CallAsync<string>(PrintHook, JsonSerializer.Serialize(new
        {
            jobId = job.Id.ToString("N"),
            documentUrl = uri.AbsoluteUri,
            title = job.Title,
            widthInches = job.WidthInches,
            heightInches = job.HeightInches
        }));
        using var registration = timeout.Token.Register(static state =>
        {
            var request = (PrintCancelRequest)state!;
            try { Electron.HostHook.Call(CancelHook, request.JobId); } catch { }
        }, new PrintCancelRequest(job.Id.ToString("N")));

        var result = await call.WaitAsync(timeout.Token);
        cancellationToken.ThrowIfCancellationRequested();

        var response = JsonSerializer.Deserialize<ElectronPrintResponse>(result, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Electron returned no print result.");
        if (response.Status == "success") return;
        if (response.Status == "canceled") throw new OperationCanceledException("Printing was canceled.", cancellationToken);
        throw new InvalidOperationException(response.Error ?? "The system print operation failed.");
    }

    private Uri ResolveDocumentUri(string value)
    {
        if (!Uri.TryCreate(_baseUri, value, out var uri) || uri.Scheme != "http" || uri.Host != _baseUri.Host || uri.Port != _baseUri.Port
            || !System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, "^/projects/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/print-sessions/[0-9a-fA-F]{32}/document$"))
            throw new InvalidOperationException("The print document URL is outside the local application host.");
        return uri;
    }

    private static Uri CreateBaseUri(IConfiguration configuration)
    {
        var host = configuration["Desktop:BindHost"];
        if (!int.TryParse(configuration["Desktop:HttpPort"], out var port) || port is < 1 or > 65535
            || (host != "localhost" && host != "127.0.0.1"))
            throw new InvalidOperationException("Desktop print requires a loopback HTTP host and port.");
        return new UriBuilder("http", host, port).Uri;
    }

    private sealed record PrintCancelRequest(string JobId);
    private sealed record ElectronPrintResponse(string Status, string? Error);
}

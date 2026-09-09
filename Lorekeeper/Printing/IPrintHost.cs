using Microsoft.JSInterop;

namespace Lorekeeper.Printing;

public interface IPrintHost
{
    bool IsDesktop { get; }

    Task PrintAsync(PrintPreparedJob job, CancellationToken cancellationToken);
}

public sealed class BrowserPrintHost(IJSRuntime js) : IPrintHost
{
    public bool IsDesktop => false;

    public async Task PrintAsync(PrintPreparedJob job, CancellationToken cancellationToken)
    {
        var module = await js.InvokeAsync<IJSObjectReference>("import", cancellationToken, "/js/print-preview.js");
        try
        {
            // A printer dialog may remain open longer than the normal JS interop timeout.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await module.InvokeVoidAsync("printDocument", timeout.Token, job.DocumentUrl);
        }
        finally
        {
            try
            {
                await module.InvokeVoidAsync("cancelPrintDocument");
                await module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
        }
    }
}

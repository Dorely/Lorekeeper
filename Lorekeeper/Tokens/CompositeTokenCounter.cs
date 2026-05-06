namespace Lorekeeper.Tokens;

public sealed class CompositeTokenCounter(
    TiktokenTokenCounter tiktoken,
    CharEstimateTokenCounter estimate,
    ILogger<CompositeTokenCounter> logger) : ITokenCounter
{
    public TokenCountResult Count(string text, TokenCountRequest? request = null)
    {
        var resolvedRequest = request ?? new TokenCountRequest();
        try
        {
            var tiktokenResult = tiktoken.TryCount(text, resolvedRequest);
            if (tiktokenResult is not null) return tiktokenResult;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Tiktoken token counting failed; falling back to character estimate.");
            return estimate.Count(text, resolvedRequest, $"Tiktoken token counting failed: {ex.Message}");
        }

        var warning = string.IsNullOrWhiteSpace(resolvedRequest.ModelName)
            ? "No tiktoken encoding was resolved for this request."
            : $"No tiktoken encoding mapping is configured for model '{resolvedRequest.ModelName}'.";
        return estimate.Count(text, resolvedRequest, warning);
    }
}
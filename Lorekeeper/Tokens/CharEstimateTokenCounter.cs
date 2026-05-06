using Microsoft.Extensions.Options;

namespace Lorekeeper.Tokens;

public sealed class CharEstimateTokenCounter(IOptions<TokenCountingOptions> options)
{
    public TokenCountResult Count(string text, TokenCountRequest request, string? warning = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new TokenCountResult(
                0,
                Method: "char-estimate",
                IsExact: false,
                ModelName: request.ModelName,
                EncodingName: request.EncodingName,
                Warning: warning);
        }

        var charsPerToken = Math.Max(1.0, options.Value.EstimatedCharsPerToken);
        var tokenCount = (int)Math.Ceiling(text.Length / charsPerToken);
        return new TokenCountResult(
            tokenCount,
            Method: "char-estimate",
            IsExact: false,
            ModelName: request.ModelName,
            EncodingName: request.EncodingName,
            Warning: warning ?? "Token count is estimated from character length because no tiktoken encoding was available for this request.");
    }
}
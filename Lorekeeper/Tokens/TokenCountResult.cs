namespace Lorekeeper.Tokens;

public sealed record TokenCountResult(
    int TokenCount,
    string Method,
    bool IsExact,
    string? EncodingName = null,
    string? ModelName = null,
    string? Warning = null);
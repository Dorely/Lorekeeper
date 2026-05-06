namespace Lorekeeper.Tokens;

public sealed record TokenCountRequest(
    string? ModelName = null,
    string? EncodingName = null);
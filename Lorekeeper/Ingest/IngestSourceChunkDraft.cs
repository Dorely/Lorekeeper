using Lorekeeper.Tokens;

namespace Lorekeeper.Ingest;

public sealed record IngestSourceChunkDraft(
    int Index,
    string Title,
    string HeadingPath,
    int StartChar,
    int EndChar,
    TokenCountResult TokenCount);
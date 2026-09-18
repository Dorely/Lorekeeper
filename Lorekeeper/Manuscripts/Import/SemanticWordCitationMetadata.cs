using Lorekeeper.Citations;

namespace Lorekeeper.Manuscripts.Import;

/// <summary>Portable citation metadata attached to editable DOCX content controls.</summary>
internal sealed record SemanticWordCitationMetadata(
    int Version,
    IReadOnlyList<CitationRecord> Records,
    IReadOnlyDictionary<string, ManuscriptCitationCluster> Clusters)
{
    public const string Namespace = "urn:lorekeeper:word-citations:v1";
    public const string TagPrefix = "lorekeeper-citation:";
}

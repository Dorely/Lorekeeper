namespace Lorekeeper.Ingest;

public interface IIngestSourceStructureBuilder
{
    IReadOnlyList<IngestSourceChunkDraft> Build(IngestSourceStructureRequest request);
}
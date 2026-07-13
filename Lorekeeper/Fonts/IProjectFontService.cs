namespace Lorekeeper.Fonts;

public interface IProjectFontService
{
    Task<IReadOnlyList<ProjectFontFamilyView>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);
    Task<ProjectFontFamilyView> ImportAsync(Guid projectId, ProjectFontUpload upload, CancellationToken cancellationToken = default);
    Task DeleteFamilyAsync(Guid projectId, Guid familyId, CancellationToken cancellationToken = default);
    Task<ProjectFontFaceData?> GetFaceDataAsync(Guid projectId, Guid faceId, CancellationToken cancellationToken = default);
    Task<ProjectFontFaceData?> ResolveFaceAsync(
        Guid projectId,
        string familyKey,
        int weight,
        bool italic,
        bool requireExact = false,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectFontUpload(string FileName, byte[] Data);

public sealed record ProjectFontFamilyView(
    string Key,
    Guid? ProjectFamilyId,
    string Name,
    string Category,
    bool IsBuiltIn,
    IReadOnlyList<ProjectFontFaceView> Faces);

public sealed record ProjectFontFaceView(
    Guid? Id,
    string SubfamilyName,
    int Weight,
    bool Italic,
    string ContentUrl);

public sealed record ProjectFontFaceData(
    Guid? Id,
    string FamilyKey,
    string FamilyName,
    string FileName,
    string ContentType,
    int Weight,
    bool Italic,
    byte[] Data);


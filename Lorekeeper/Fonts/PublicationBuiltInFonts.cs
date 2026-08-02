namespace Lorekeeper.Fonts;

public static class PublicationBuiltInFonts
{
    public const string DefaultKey = "builtin:andika";

    public static readonly IReadOnlyList<ProjectFontFamilyView> Families =
    [
        Family("builtin:atkinson-hyperlegible", "Atkinson Hyperlegible", "Readable", "atkinson-hyperlegible",
            Face("Regular", 400), Face("Italic", 400, true), Face("Bold", 700), Face("Bold Italic", 700, true)),
        Family(DefaultKey, "Andika", "Readable", "andika",
            Face("Regular", 400), Face("Italic", 400, true), Face("Bold", 700), Face("Bold Italic", 700, true)),
        Family("builtin:lexend", "Lexend", "Readable", "lexend",
            Face("Regular", 400), Face("Bold", 700)),
        Family("builtin:nunito", "Nunito", "Readable", "nunito",
            Face("Regular", 400), Face("Italic", 400, true), Face("Bold", 700), Face("Bold Italic", 700, true)),
        Family("builtin:lora", "Lora", "Story serif", "lora",
            Face("Regular", 400), Face("Italic", 400, true), Face("Bold", 700), Face("Bold Italic", 700, true)),
        Family("builtin:merriweather", "Merriweather", "Story serif", "merriweather",
            Face("Regular", 400), Face("Italic", 400, true), Face("Bold", 700), Face("Bold Italic", 700, true)),
        Family("builtin:fredoka", "Fredoka", "Display", "fredoka",
            Face("Regular", 400), Face("Bold", 700)),
        Family("builtin:balsamiq-sans", "Balsamiq Sans", "Display", "balsamiq-sans",
            Face("Regular", 400), Face("Italic", 400, true), Face("Bold", 700), Face("Bold Italic", 700, true)),
        Family("builtin:patrick-hand", "Patrick Hand", "Handwritten", "patrick-hand",
            Face("Regular", 400)),
        Family("builtin:kalam", "Kalam", "Handwritten", "kalam",
            Face("Regular", 400), Face("Bold", 700)),
        Family("builtin:roboto-mono", "Roboto Mono", "Monospace", "roboto-mono",
            Face("Regular", 400), Face("Italic", 400, true), Face("Bold", 700), Face("Bold Italic", 700, true)),
    ];

    public static ProjectFontFamilyView? Find(string key) =>
        Families.FirstOrDefault(family => string.Equals(family.Key, key, StringComparison.OrdinalIgnoreCase));

    private static ProjectFontFamilyView Family(
        string key,
        string name,
        string category,
        string directory,
        params BuiltInFace[] faces) =>
        new(
            key,
            ProjectFamilyId: null,
            name,
            category,
            IsBuiltIn: true,
            EmbeddingRightsConfirmed: true,
            RightsDeclaration: "Bundled under the font license recorded in the Lorekeeper distribution notices.",
            faces.Select(face => new ProjectFontFaceView(
                Id: null,
                face.SubfamilyName,
                face.Weight,
                face.Italic,
                $"/fonts/{directory}/{FileName(name, face)}")).ToList());

    private static BuiltInFace Face(string subfamilyName, int weight, bool italic = false) =>
        new(subfamilyName, weight, italic);

    private static string FileName(string familyName, BuiltInFace face)
    {
        var stem = familyName.Replace(" ", string.Empty, StringComparison.Ordinal);
        return $"{stem}-{face.SubfamilyName.Replace(" ", string.Empty, StringComparison.Ordinal)}.ttf";
    }

    private sealed record BuiltInFace(string SubfamilyName, int Weight, bool Italic);
}

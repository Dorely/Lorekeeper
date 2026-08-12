using Lorekeeper.Models;

namespace Lorekeeper.Publish;

public static class PublicationReleaseNaming
{
    public static string DefaultName(PublicationEditionFormat format, PublicationVendor vendor) => format switch
    {
        PublicationEditionFormat.Paperback => vendor switch
        {
            PublicationVendor.AmazonKdp => "Amazon KDP Paperback",
            PublicationVendor.IngramSpark => "IngramSpark Paperback",
            _ => "Generic Paperback",
        },
        PublicationEditionFormat.Epub => "EPUB Ebook",
        _ => "PDF Ebook",
    };

    public static string AllocateUnique(string requestedName, IEnumerable<string> existingNames)
    {
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(requestedName))
            return requestedName;

        for (var number = 2; number < int.MaxValue; number++)
        {
            var suffix = $" {number}";
            var prefixLength = Math.Min(requestedName.Length, 120 - suffix.Length);
            var candidate = requestedName[..prefixLength].TrimEnd() + suffix;
            if (!names.Contains(candidate))
                return candidate;
        }

        throw new InvalidOperationException("A unique publication release name could not be allocated.");
    }
}

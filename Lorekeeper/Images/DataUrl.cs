namespace Lorekeeper.Images;

public static class DataUrl
{
    public static string ToDataUrl(string contentType, byte[] data) =>
        $"data:{contentType};base64,{Convert.ToBase64String(data)}";

    public static (string ContentType, byte[] Data) Parse(string dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl))
            throw new ArgumentException("Data URL is required.", nameof(dataUrl));

        const string marker = ";base64,";
        if (!dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Value is not a data URL.");

        var markerIndex = dataUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            throw new FormatException("Data URL is not base64 encoded.");

        var contentType = dataUrl["data:".Length..markerIndex].Trim();
        if (string.IsNullOrWhiteSpace(contentType))
            throw new FormatException("Data URL content type is missing.");

        var data = Convert.FromBase64String(dataUrl[(markerIndex + marker.Length)..]);
        return (contentType, data);
    }
}

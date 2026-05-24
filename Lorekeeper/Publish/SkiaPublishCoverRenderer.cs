using System.Globalization;
using SkiaSharp;

namespace Lorekeeper.Publish;

public sealed class SkiaPublishCoverRenderer : IPublishCoverRenderer
{
    private const string RenderedContentType = "image/png";
    private const float LineHeightMultiplier = 1.05f;

    public PublishAssetDocument Render(
        PublishAssetDocument cover,
        PublishCoverLayoutView layout,
        string title,
        string subtitle,
        string author)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(cover.Data);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
                return cover;

            var imageInfo = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(imageInfo);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(bitmap, 0, 0);

            foreach (var layer in layout.Layers)
            {
                var text = TextForLayer(layer.Kind, title, subtitle, author);
                if (!layer.IsVisible || string.IsNullOrWhiteSpace(text))
                    continue;

                DrawLayer(canvas, imageInfo, layer, text.Trim());
            }

            canvas.Flush();
            using var rendered = surface.Snapshot();
            using var data = rendered.Encode(SKEncodedImageFormat.Png, quality: 100);
            return cover with
            {
                FileName = RenderedFileName(cover.FileName),
                ContentType = RenderedContentType,
                Data = data.ToArray(),
            };
        }
        catch (Exception)
        {
            return cover;
        }
    }

    private static void DrawLayer(SKCanvas canvas, SKImageInfo imageInfo, PublishCoverLayerView layer, string text)
    {
        var fontSize = Math.Max(1, imageInfo.Width * ClampPercent(layer.FontSizePercent) / 100f);
        using var typeface = SKTypeface.FromFamilyName(
            FontFamily(layer.FontFamily),
            layer.IsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            layer.IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        using var font = new SKFont(typeface ?? SKTypeface.Default, fontSize)
        {
            Edging = SKFontEdging.Antialias,
            Hinting = SKFontHinting.Normal,
            Subpixel = true,
        };
        using var paint = new SKPaint
        {
            Color = TextColor(layer.Color, layer.Opacity),
            IsAntialias = true,
        };

        var blockWidth = Math.Max(1, imageInfo.Width * ClampPercent(layer.WidthPercent) / 100f);
        var centerX = imageInfo.Width * ClampPercent(layer.XPercent) / 100f;
        var centerY = imageInfo.Height * ClampPercent(layer.YPercent) / 100f;
        var lines = WrapText(text, blockWidth, font, paint);
        if (lines.Count == 0)
            return;

        var metrics = font.Metrics;
        var lineHeight = Math.Max(fontSize * LineHeightMultiplier, metrics.Descent - metrics.Ascent + metrics.Leading);
        var blockHeight = ((lines.Count - 1) * lineHeight) + metrics.Descent - metrics.Ascent;
        var baseline = centerY - (blockHeight / 2f) - metrics.Ascent;
        var textAlign = TextAlign(layer.TextAlign);
        var x = layer.TextAlign switch
        {
            PublishCoverTextAlign.Left => centerX - (blockWidth / 2f),
            PublishCoverTextAlign.Right => centerX + (blockWidth / 2f),
            _ => centerX,
        };

        for (var i = 0; i < lines.Count; i++)
        {
            var y = baseline + (i * lineHeight);
            DrawShadow(canvas, lines[i], x, y, textAlign, font, layer.Shadow, fontSize, layer.Opacity);
            canvas.DrawText(lines[i], x, y, textAlign, font, paint);
        }
    }

    private static IReadOnlyList<string> WrapText(string text, float maxWidth, SKFont font, SKPaint paint)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(paragraph))
                continue;

            var current = string.Empty;
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current.Length == 0)
                {
                    AddWord(word);
                    continue;
                }

                var candidate = current + " " + word;
                if (Measure(candidate, font, paint) <= maxWidth)
                {
                    current = candidate;
                    continue;
                }

                lines.Add(current);
                current = string.Empty;
                AddWord(word);
            }

            if (current.Length > 0)
                lines.Add(current);

            void AddWord(string word)
            {
                if (Measure(word, font, paint) <= maxWidth)
                {
                    current = word;
                    return;
                }

                foreach (var segment in BreakLongWord(word, maxWidth, font, paint))
                {
                    if (current.Length == 0)
                    {
                        current = segment;
                        continue;
                    }

                    lines.Add(current);
                    current = segment;
                }
            }
        }

        return lines;
    }

    private static IEnumerable<string> BreakLongWord(string word, float maxWidth, SKFont font, SKPaint paint)
    {
        var segment = string.Empty;
        foreach (var rune in word.EnumerateRunes())
        {
            var candidate = segment + rune;
            if (segment.Length > 0 && Measure(candidate, font, paint) > maxWidth)
            {
                yield return segment;
                segment = rune.ToString();
                continue;
            }

            segment = candidate;
        }

        if (segment.Length > 0)
            yield return segment;
    }

    private static void DrawShadow(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign textAlign,
        SKFont font,
        PublishCoverShadow shadow,
        float fontSize,
        double opacity)
    {
        if (shadow == PublishCoverShadow.None)
            return;

        switch (shadow)
        {
            case PublishCoverShadow.Glow:
                DrawShadowText(canvas, text, x, y, textAlign, font, fontSize * 0.08f, ShadowColor(255, 255, 255, 180, opacity), 0, 0);
                DrawShadowText(canvas, text, x, y, textAlign, font, fontSize * 0.045f, ShadowColor(0, 0, 0, 145, opacity), 0, fontSize * 0.025f);
                break;
            case PublishCoverShadow.Strong:
                DrawShadowText(canvas, text, x, y, textAlign, font, fontSize * 0.08f, ShadowColor(0, 0, 0, 185, opacity), 0, fontSize * 0.045f);
                DrawShadowText(canvas, text, x, y, textAlign, font, 0, ShadowColor(0, 0, 0, 220, opacity), 0, fontSize * 0.018f);
                break;
            default:
                DrawShadowText(canvas, text, x, y, textAlign, font, fontSize * 0.055f, ShadowColor(0, 0, 0, 155, opacity), 0, fontSize * 0.03f);
                break;
        }
    }

    private static void DrawShadowText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign textAlign,
        SKFont font,
        float blur,
        SKColor color,
        float offsetX,
        float offsetY)
    {
        using var paint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
        };
        using var maskFilter = blur > 0 ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur) : null;
        paint.MaskFilter = maskFilter;
        canvas.DrawText(text, x + offsetX, y + offsetY, textAlign, font, paint);
    }

    private static SKColor ShadowColor(byte red, byte green, byte blue, byte alpha, double opacity) =>
        new(red, green, blue, (byte)Math.Round(alpha * Math.Clamp(opacity, 0, 1)));

    private static float Measure(string text, SKFont font, SKPaint paint) =>
        font.MeasureText(text, paint);

    private static string TextForLayer(PublishCoverLayerKind kind, string title, string subtitle, string author) =>
        kind switch
        {
            PublishCoverLayerKind.Title => title,
            PublishCoverLayerKind.Subtitle => subtitle,
            PublishCoverLayerKind.Author => author,
            _ => string.Empty,
        };

    private static string FontFamily(PublishCoverFontFamily fontFamily) =>
        fontFamily switch
        {
            PublishCoverFontFamily.Sans => "Arial",
            PublishCoverFontFamily.Display => "Trebuchet MS",
            PublishCoverFontFamily.Monospace => "Consolas",
            _ => "Georgia",
        };

    private static SKTextAlign TextAlign(PublishCoverTextAlign textAlign) =>
        textAlign switch
        {
            PublishCoverTextAlign.Left => SKTextAlign.Left,
            PublishCoverTextAlign.Right => SKTextAlign.Right,
            _ => SKTextAlign.Center,
        };

    private static SKColor TextColor(string color, double opacity)
    {
        var hex = color.TrimStart('#');
        if (hex.Length != 6
            || !byte.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red)
            || !byte.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green)
            || !byte.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            red = green = blue = byte.MaxValue;
        }

        var alpha = (byte)Math.Round(byte.MaxValue * Math.Clamp(opacity, 0, 1));
        return new SKColor(red, green, blue, alpha);
    }

    private static float ClampPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;

        return (float)Math.Clamp(value, 0, 100);
    }

    private static string RenderedFileName(string fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName)
            ? "cover"
            : Path.GetFileNameWithoutExtension(fileName.Trim());
        return $"{name}-rendered.png";
    }
}

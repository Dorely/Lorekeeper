using System.Runtime.CompilerServices;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;
using Microsoft.AspNetCore.Components;

namespace Lorekeeper.Components.Chat;

/// <summary>
/// Renders assistant chat prose as display-only Markdown. Raw HTML is escaped, and model-authored
/// links and images render as inert text so a reply can never navigate the app, hand off to an
/// external browser, or fetch remote content.
/// </summary>
internal static class ChatMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseSoftlineBreakAsHardlineBreak()
        .DisableHtml()
        .Build();

    // Every transcript re-render revisits completed messages; reuse markup until the source text changes.
    private static readonly ConditionalWeakTable<StringBuilder, RenderedMarkdown> Cache = new();

    public static MarkupString Render(StringBuilder source)
    {
        var markdown = source.ToString();
        if (Cache.TryGetValue(source, out var cached) && string.Equals(cached.Source, markdown, StringComparison.Ordinal))
            return cached.Markup;

        var rendered = new RenderedMarkdown(markdown, Render(markdown));
        Cache.AddOrUpdate(source, rendered);
        return rendered.Markup;
    }

    private static MarkupString Render(string markdown)
    {
        using var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);
        renderer.ObjectRenderers.ReplaceOrAdd<LinkInlineRenderer>(new InertLinkRenderer());
        renderer.ObjectRenderers.ReplaceOrAdd<AutolinkInlineRenderer>(new InertAutolinkRenderer());
        renderer.Render(Markdown.Parse(markdown, Pipeline));
        writer.Flush();
        return new MarkupString(writer.ToString());
    }

    private sealed record RenderedMarkdown(string Source, MarkupString Markup);

    private sealed class InertLinkRenderer : HtmlObjectRenderer<LinkInline>
    {
        protected override void Write(HtmlRenderer renderer, LinkInline link)
        {
            renderer.Write(link.IsImage ? "<span class=\"chat-md-image\"" : "<span class=\"chat-md-link\"");
            if (!string.IsNullOrWhiteSpace(link.Url))
                renderer.Write(" title=\"").WriteEscape(link.Url).Write('"');
            renderer.Write('>');
            renderer.WriteChildren(link);
            renderer.Write("</span>");
        }
    }

    private sealed class InertAutolinkRenderer : HtmlObjectRenderer<AutolinkInline>
    {
        protected override void Write(HtmlRenderer renderer, AutolinkInline link) =>
            renderer.Write("<span class=\"chat-md-link\">").WriteEscape(link.Url).Write("</span>");
    }
}

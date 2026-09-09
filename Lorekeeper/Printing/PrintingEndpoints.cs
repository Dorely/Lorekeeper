using System.Net;
using System.Text;

namespace Lorekeeper.Printing;

public static class PrintingEndpoints
{
    public static IEndpointRouteBuilder MapPrintingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/projects/{projectId:guid}/print-sessions/{sessionId:guid}/document", (Guid projectId, Guid sessionId, PrintSessionStore store, HttpContext context) =>
        {
            if (!store.TryGet(sessionId, out var session) || session!.Job.ProjectId != projectId) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Pragma = "no-cache"; context.Response.Headers.XContentTypeOptions = "nosniff";
            var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18));
            context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src 'nonce-{nonce}'; font-src 'self'; frame-ancestors 'self'";
            var sheets = string.Join("\n", session.Pages.Select((_, i) => $"<section class=\"sheet\"><img src=\"/projects/{projectId}/print-sessions/{sessionId:N}/sheets/{i + 1}.png\" decoding=\"async\" /></section>"));
            var width = session.Job.WidthInches.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); var height = session.Job.HeightInches.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var html = $"<!doctype html><html><head><meta charset=utf-8><title>{WebUtility.HtmlEncode(session.Job.Title)}</title><style>@page{{size:{width}in {height}in;margin:0}}html,body{{margin:0;padding:0;background:white}}.sheet{{width:{width}in;height:{height}in;page-break-after:always;break-after:page;overflow:hidden;print-color-adjust:exact;-webkit-print-color-adjust:exact}}.sheet:last-of-type{{page-break-after:auto;break-after:auto}}img{{display:block;width:100%;height:100%;object-fit:contain}}</style></head><body data-lorekeeper-print-ready=\"false\">{sheets}<script nonce=\"{nonce}\">window.lorekeeperPrintError='';window.lorekeeperPrintReady=(async()=>{{try{{await document.fonts.ready;for(const image of document.images){{if(!image.complete)await new Promise((resolve,reject)=>{{image.onload=resolve;image.onerror=reject}});if(image.decode)await image.decode()}}document.body.dataset.lorekeeperPrintReady='true';return true}}catch(error){{window.lorekeeperPrintError=String(error);document.body.dataset.lorekeeperPrintReady='error';throw error}}}})();</script></body></html>";
            return Results.Text(html, "text/html; charset=utf-8", Encoding.UTF8);
        });

        endpoints.MapGet("/projects/{projectId:guid}/print-sessions/{sessionId:guid}/sheets/{page:int}.png", (Guid projectId, Guid sessionId, int page, PrintSessionStore store, HttpContext context) =>
        {
            if (!store.TryGet(sessionId, out var session) || session!.Job.ProjectId != projectId || page < 1 || page > session.Pages.Count) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store"; context.Response.Headers.Pragma = "no-cache"; context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(session.Pages[page - 1], "image/png");
        });
        return endpoints;
    }
}

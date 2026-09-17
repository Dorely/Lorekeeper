using System.Net;

namespace Lorekeeper.Authorization;

public static class OpenAiAuthorizationCompletionPage
{
    public static string Render(OpenAiAuthorizationCallbackResult result)
    {
        var title = result.Status == OpenAiAuthorizationFlowStatus.Succeeded
            ? "Connection complete"
            : "Connection not completed";
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{WebUtility.HtmlEncode(title)}} · Lorekeeper</title>
              <style>
                :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
                body { display: grid; min-height: 100vh; margin: 0; place-items: center; background: #111827; color: #f9fafb; }
                main { width: min(34rem, calc(100% - 3rem)); padding: 2rem; border: 1px solid #374151; border-radius: 1rem; background: #1f2937; }
                h1 { margin-top: 0; font-size: 1.6rem; }
                p { line-height: 1.55; color: #d1d5db; }
              </style>
            </head>
            <body>
              <main>
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                <p>{{WebUtility.HtmlEncode(result.Message)}}</p>
                <p>You can close this browser tab and return to Lorekeeper.</p>
              </main>
            </body>
            </html>
            """;
    }
}

namespace TabBrowser.Services;

internal static class UserGuidePageHtmlBuilder
{
    public static string BuildUnavailable()
    {
        return """
            <!doctype html>
            <html lang="ja">
            <head>
              <meta charset="utf-8">
              <meta name="color-scheme" content="dark">
              <title>ユーザーガイド</title>
              <style>
                html, body { min-height: 100%; }
                body {
                  box-sizing: border-box;
                  margin: 0;
                  padding: 32px;
                  background: #0d1117;
                  color: #e6edf3;
                  font-family: "Yu Gothic UI", "Meiryo", sans-serif;
                }
                main {
                  max-width: 760px;
                  margin: 0 auto;
                  padding: 24px;
                  border: 1px solid #30363d;
                  background: #10151d;
                }
                h1 { margin-top: 0; font-size: 24px; }
                p { color: #b7c0cc; line-height: 1.8; }
                code { color: #79c0ff; }
              </style>
            </head>
            <body>
              <main>
                <h1>ユーザーガイドを表示できません</h1>
                <p><code>Help\user-guide.html</code>が配布物に含まれているか確認してください。</p>
              </main>
            </body>
            </html>
            """;
    }
}

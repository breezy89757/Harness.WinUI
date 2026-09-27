// Yoke — Licensed under the MIT License.

using System.Net;
using Yoke.Core.Artifacts;
using Yoke.Core.Config;
using Yoke.MarkdownRendering;

namespace Yoke.App.Artifacts;

/// <param name="SourceFile">File name of the raw content (what "Save as" copies).</param>
/// <param name="PreviewFile">File name the panel navigates to under <see cref="ArtifactStore.HostName"/>.</param>
public sealed record StoredArtifact(string Id, ArtifactType Type, string Title, string Content, int Version, string SourceFile, string PreviewFile)
{
    public string SourcePath => Path.Combine(ArtifactStore.Folder, SourceFile);
    public string PreviewPath => Path.Combine(ArtifactStore.Folder, PreviewFile);
    public string PreviewUrl => $"https://{ArtifactStore.HostName}/{Uri.EscapeDataString(PreviewFile)}?v={Version}";
}

/// <summary>
/// Writes completed artifacts to the artifacts folder under the data folder (<see cref="AppPaths"/>) and builds a preview page for types
/// that need one (SVG, Mermaid, Markdown). The folder is served to the artifact panel's own WebView2
/// under <see cref="HostName"/> — a different origin from the chat, with no bridge to the app.
/// </summary>
public sealed class ArtifactStore
{
    public const string HostName = "yoke.artifacts";

    public static string Folder => AppPaths.Combine("artifacts");

    private readonly Dictionary<string, StoredArtifact> _artifacts = new(StringComparer.Ordinal);

    public StoredArtifact? Get(string id) => _artifacts.GetValueOrDefault(id);

    public StoredArtifact Save(ArtifactSegment artifact)
    {
        Directory.CreateDirectory(Folder);
        var version = (_artifacts.GetValueOrDefault(artifact.Id)?.Version ?? 0) + 1;

        var sourceFile = artifact.Id + artifact.Type switch
        {
            ArtifactType.Svg => ".svg",
            ArtifactType.Mermaid => ".mmd",
            ArtifactType.Markdown => ".md",
            _ => ".html",
        };
        File.WriteAllText(Path.Combine(Folder, sourceFile), artifact.Content);

        var previewFile = sourceFile;
        if (artifact.Type != ArtifactType.Html)
        {
            previewFile = artifact.Id + ".preview.html";
            File.WriteAllText(Path.Combine(Folder, previewFile), BuildPreview(artifact, sourceFile));
        }

        var stored = new StoredArtifact(artifact.Id, artifact.Type, artifact.Title, artifact.Content, version, sourceFile, previewFile);
        _artifacts[artifact.Id] = stored;
        return stored;
    }

    private static string BuildPreview(ArtifactSegment artifact, string sourceFile)
    {
        var title = WebUtility.HtmlEncode(artifact.Title);
        var assets = "https://" + ChatShell.VirtualHostName;
        return artifact.Type switch
        {
            // <img> rather than inline SVG: scripts inside an SVG never run this way.
            ArtifactType.Svg => $$"""
                <!DOCTYPE html><html><head><meta charset="utf-8"><title>{{title}}</title>
                <style>html,body{margin:0;height:100%;background:#fff}body{display:grid;place-items:center}img{max-width:100%;max-height:100vh}</style>
                </head><body><img src="{{Uri.EscapeDataString(sourceFile)}}" alt="{{title}}"></body></html>
                """,
            ArtifactType.Mermaid => $$"""
                <!DOCTYPE html><html><head><meta charset="utf-8"><title>{{title}}</title>
                <style>body{margin:0;padding:24px;font-family:'Segoe UI',sans-serif;background:#fff}.mermaid{display:flex;justify-content:center}.mermaid svg{max-width:100%;height:auto}</style>
                </head><body><pre class="mermaid">{{WebUtility.HtmlEncode(artifact.Content)}}</pre>
                <script src="{{assets}}/js/mermaid.min.js"></script>
                <script>mermaid.initialize({ startOnLoad: true, securityLevel: 'strict' });</script>
                </body></html>
                """,
            _ => $$"""
                <!DOCTYPE html><html><head><meta charset="utf-8"><title>{{title}}</title>
                <link rel="stylesheet" href="{{assets}}/css/github.min.css">
                <style>
                body{margin:0 auto;max-width:860px;padding:32px 40px;font:15px/1.7 'Segoe UI','Microsoft JhengHei UI',sans-serif;color:#24292f;background:#fff}
                h1,h2{border-bottom:1px solid #d0d7de;padding-bottom:.3em}
                pre{background:#f6f8fa;padding:14px;border-radius:8px;overflow:auto}
                code{font-family:'Cascadia Code',Consolas,monospace;font-size:.9em}
                table{border-collapse:collapse}th,td{border:1px solid #d0d7de;padding:6px 12px}
                blockquote{margin:0;padding:0 1em;color:#57606a;border-left:4px solid #d0d7de}
                img{max-width:100%}
                </style></head><body>
                {{ChatMarkdownRenderer.RenderBody(artifact.Content)}}
                <script src="{{assets}}/js/highlight.min.js"></script>
                <script src="{{assets}}/js/mermaid.min.js"></script>
                <script>
                hljs.highlightAll();
                mermaid.initialize({ startOnLoad: false, securityLevel: 'strict' });
                mermaid.run({ querySelector: '[data-mermaid-hash], pre.mermaid' });
                </script></body></html>
                """,
        };
    }
}

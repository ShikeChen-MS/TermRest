using System.Text;
using TermRest.Core.Certificates;

namespace TermRest.Core.Http;

/// <summary>Renders a prepared request as a POSIX-shell cURL command.</summary>
public static class CurlExporter
{
    public static string Export(PreparedRequest request, bool multiline = true)
    {
        var parts = new List<string> { "curl" };
        if (request.Method != "GET")
        {
            parts.Add(request.Method == "HEAD" ? "--head" : $"-X {request.Method}");
        }

        parts.Add(Quote(request.Url));
        foreach (var (key, value) in request.Headers)
        {
            parts.Add($"-H {Quote($"{key}: {value}")}");
        }

        switch (request.Body.Kind)
        {
            case PreparedBodyKind.Text:
                parts.Add($"--data-raw {Quote(request.Body.Text ?? "")}");
                if (request.GetHeader("Content-Type") is null && request.Body.ContentType is { } contentType)
                {
                    parts.Add($"-H {Quote($"Content-Type: {contentType}")}");
                }

                break;
            case PreparedBodyKind.FormUrlEncoded:
                parts.AddRange(request.Body.Fields.Select(f => $"--data-urlencode {Quote($"{f.Key}={f.Value}")}"));
                break;
            case PreparedBodyKind.Multipart:
                parts.AddRange(request.Body.Parts.Select(p => p.FilePath is null ? $"-F {Quote($"{p.Name}={p.Value}")}" : $"-F {Quote($"{p.Name}=@{p.FilePath}")}"));
                break;
            case PreparedBodyKind.File:
                parts.Add($"--data-binary {Quote("@" + request.Body.FilePath)}");
                break;
        }

        if (!request.StrictSsl)
        {
            parts.Add("--insecure");
        }

        if (request.Credential is { } credential)
        {
            parts.Add($"--anyauth -u {Quote($"{(string.IsNullOrEmpty(credential.Domain) ? "" : credential.Domain + "\\")}{credential.UserName}:{credential.Password}")}");
        }

        if (request.ClientCertificate is { } selection)
        {
            var entry = selection.Entry;
            switch (entry.Kind)
            {
                case CertificateSourceKind.Pem when entry.Cert is not null:
                    parts.Add($"--cert {Quote(entry.Cert.Src)}");
                    if (entry.Key is not null)
                    {
                        parts.Add($"--key {Quote(entry.Key.Src)}");
                    }

                    break;
                case CertificateSourceKind.Pfx:
                    parts.Add($"--cert-type P12 --cert {Quote(entry.Pfx!.Src)}");
                    break;
                default:
                    parts.Add($"# client certificate from OS store: {selection.Certificate.Summary.Subject} ({selection.Certificate.Certificate.Thumbprint})");
                    break;
            }
        }

        if (!request.FollowRedirects)
        {
            return Join(parts, multiline);
        }

        parts.Add("--location");
        return Join(parts, multiline);
    }

    private static string Join(List<string> parts, bool multiline)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(multiline ? " \\\n  " : " ");
            }

            builder.Append(parts[i]);
        }

        return builder.ToString();
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

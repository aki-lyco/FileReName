using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Explore.Build
{
    public sealed record AiClassifyRequest(
      string BasePath, string UncategorizedRelPath,
      IReadOnlyList<CategoryDef> Categories,
      FileMeta File, string ExtractedText
    );

    public sealed record CategoryDef(
      string RelPath, string Display, string[]? Keywords, string? ExtFilter, string? AiHint
    );

    public sealed record FileMeta(
      string Name, string Ext, string FullPath, DateTimeOffset Mtime, long SizeBytes
    );

    public sealed record AiClassifyResult(
      string ClassifiedRelPath, double Confidence, string Summary, string[] Tags, string Reason
    );

    public interface IAiClassifier
    {
        Task<AiClassifyResult> ClassifyAsync(AiClassifyRequest req, CancellationToken ct);
    }
}

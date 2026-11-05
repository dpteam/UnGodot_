using System.Diagnostics;

namespace UnGodot.Dump;

/// <summary>Scans a memory source and carves every artifact the real parsers accept.</summary>
public static class MemoryDump
{
    public static DumpReport Run(
        IMemorySource source,
        string outputDirectory,
        int engineMajor = 3,
        CancellationToken cancellationToken = default)
    {
        var carver = new ArtifactCarver(outputDirectory, engineMajor);
        var watch = Stopwatch.StartNew();
        carver.Report.BytesScanned = MemoryScanner.Scan(
            source,
            MemorySignatures.All,
            (signature, offset) => carver.Handle(signature, source, offset),
            cancellationToken: cancellationToken);
        carver.Report.Elapsed = watch.Elapsed;
        return carver.Report;
    }
}
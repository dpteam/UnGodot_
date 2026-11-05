using System.Text.Json;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.GDScript;

namespace UnGodot.Cli;

internal static class DecompileCommand
{
    public static int Run(CliOptions options, CancellationToken cancellationToken)
    {
        string input = options.Positionals.Count > 0 ? options.Positionals[0] : string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new UsageException("decompile needs a .gdc file or a folder");
        }

        bool singleFile = File.Exists(input);
        List<string> files;
        if (singleFile)
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory
                .EnumerateFiles(input, "*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".gde", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            Console.Error.WriteLine($"ungodot: '{input}' does not exist");
            return 3;
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("ungodot: no .gdc/.gde scripts found");
            return 3;
        }

        string outputDirectory = options.Out ?? (singleFile
            ? Path.GetDirectoryName(Path.GetFullPath(input))!
            : Path.GetFullPath(input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "_decompiled"));

        var decompileOptions = new DecompileOptions
        {
            UseSpaces = options.Spaces,
            TabSize = options.TabSize,
        };

        int written = 0;
        int failed = 0;
        var revisions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = singleFile
                ? Path.GetFileName(file)
                : Path.GetRelativePath(input, file);

            try
            {
                if (file.EndsWith(".gde", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GdScriptBytecodeException("encrypted GDScript (.gde) is not supported yet");
                }

                byte[] bytecode = File.ReadAllBytes(file);
                RevisionDetection detection = RevisionDetector.Detect(bytecode, options.EngineVersion);
                if (detection.Revision is null)
                {
                    throw new GdScriptBytecodeException(detection.Error ?? "cannot determine the bytecode revision");
                }

                DecompileResult result = GdScriptDecompiler.Decompile(bytecode, detection.Revision, decompileOptions);

                string target = Path.Combine(outputDirectory, Path.ChangeExtension(relative, ".gd"));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(PathUtil.FullPath(target), result.Source);

                string revisionLabel = $"{detection.Revision.EngineVersion} ({detection.Revision.Rev})";
                revisions[revisionLabel] = revisions.GetValueOrDefault(revisionLabel) + 1;
                written++;
                Log.Debug($"{relative} -> {detection.Revision.EngineVersion}");
            }
            catch (Exception ex) when (ex is GdScriptBytecodeException or GdScriptDecompileException or IOException)
            {
                failed++;
                failures.Add($"{relative}: {ex.Message}");
                Log.Warn($"{relative}: {ex.Message}");
            }
        }

        string[] revisionSummary = revisions
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        if (options.Json)
        {
            var payload = new
            {
                input,
                output = outputDirectory,
                scripts = files.Count,
                written,
                failed,
                revisions,
                failures,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.Out.WriteLine($"scripts          {files.Count}");
            Console.Out.WriteLine($"decompiled       {written}");
            if (failed > 0)
            {
                Console.Out.WriteLine($"failed           {failed}");
            }

            if (revisionSummary.Length > 0)
            {
                Console.Out.WriteLine($"bytecode         {string.Join(", ", revisionSummary)}");
            }

            Console.Out.WriteLine($"output           {outputDirectory}");
        }

        return failed == 0 ? 0 : 1;
    }
}
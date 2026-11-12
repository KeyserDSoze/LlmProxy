using System.Globalization;
using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

public sealed record HubFile(string Path, long? Size, string? Sha256, string? BlobId);

/// <summary>
/// GGUF file selection mirrored from llama.cpp common/download.cpp
/// (find_best_model, get_split_files, find_best_sibling) so that a pinned
/// download selects the same file llama-server would pick for repo[:quant].
/// </summary>
public static class GgufFileSelector
{
    public sealed record SplitInfo(string Prefix, string Tag, int Index, int Count);

    private static readonly string[] SidecarKeywords = ["mmproj", "mtp-", "dflash-", "eagle3-", "dspark-"];
    private static readonly string[] DefaultTags = ["Q4_K_M", "Q8_0"];
    private static readonly Regex Split = new(@"^(.+)-([0-9]{5})-of-([0-9]{5})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Tag = new(@"[-.]([A-Z0-9_]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Repository = new(
        @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,95}/[A-Za-z0-9][A-Za-z0-9_.-]{0,95}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex QuantTag = new(@"^[A-Za-z0-9_.-]{1,50}$", RegexOptions.CultureInvariant);

    public static (string Repository, string? Tag) SplitRepositoryTag(string providerModelName)
    {
        var value = providerModelName.Trim();
        var colon = value.IndexOf(':');
        var repository = colon < 0 ? value : value[..colon];
        var tag = colon < 0 ? null : value[(colon + 1)..];
        if (!Repository.IsMatch(repository) || repository.Contains("--", StringComparison.Ordinal) ||
            repository.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException(
                $"'{providerModelName}' is not a valid Hugging Face repository. Use org/model or org/model:QUANT.");
        if (tag is not null && !QuantTag.IsMatch(tag))
            throw new ArgumentException($"'{tag}' is not a valid GGUF quantization tag (example: Q4_K_M).");
        return (repository, string.IsNullOrEmpty(tag) ? null : tag);
    }

    public static SplitInfo? GetSplitInfo(string path)
    {
        if (!path.EndsWith(".gguf", StringComparison.Ordinal)) return null;
        var prefix = path[..^".gguf".Length];
        int index = 1, count = 1;
        var split = Split.Match(prefix);
        if (split.Success)
        {
            index = int.Parse(split.Groups[2].Value, CultureInfo.InvariantCulture);
            count = int.Parse(split.Groups[3].Value, CultureInfo.InvariantCulture);
            prefix = split.Groups[1].Value;
        }
        var tag = Tag.Match(prefix);
        return new SplitInfo(prefix, tag.Success ? tag.Groups[1].Value.ToUpperInvariant() : "", index, count);
    }

    public static bool IsModelFile(string path)
    {
        if (!path.EndsWith(".gguf", StringComparison.Ordinal)) return false;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return !name.Contains("imatrix", StringComparison.Ordinal) &&
               !SidecarKeywords.Any(keyword => name.Contains(keyword, StringComparison.Ordinal));
    }

    private static bool IsFirstPart(string path) =>
        GetSplitInfo(path) is not { Count: > 1, Index: not 1 };

    public static HubFile? FindBestModel(IReadOnlyList<HubFile> files, string? tag)
    {
        foreach (var candidate in string.IsNullOrEmpty(tag) ? DefaultTags : [tag])
        {
            // Match the tag as a full token so ":F16" never selects a BF16 file.
            var pattern = new Regex("(^|[-./])" + Regex.Escape(candidate) + "[.-]",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var match = files.FirstOrDefault(file =>
                IsModelFile(file.Path) && pattern.IsMatch(file.Path) && IsFirstPart(file.Path));
            if (match is not null) return match;
        }
        return string.IsNullOrEmpty(tag)
            ? files.FirstOrDefault(file => IsModelFile(file.Path) && IsFirstPart(file.Path))
            : null;
    }

    public static IReadOnlyList<HubFile> SplitFiles(IReadOnlyList<HubFile> files, HubFile primary)
    {
        var split = GetSplitInfo(primary.Path);
        if (split is null || split.Count <= 1) return [primary];
        return files
            .Select(file => (file, info: GetSplitInfo(file.Path)))
            .Where(item => item.info is { } info && info.Count == split.Count && info.Prefix == split.Prefix)
            .OrderBy(item => item.info!.Index)
            .Select(item => item.file)
            .ToArray();
    }

    public static IReadOnlyList<string> ExpectedSplitPaths(string primaryPath)
    {
        var split = GetSplitInfo(primaryPath);
        if (split is null || split.Count <= 1) return [primaryPath];
        return Enumerable.Range(1, split.Count)
            .Select(index => string.Create(CultureInfo.InvariantCulture,
                $"{split.Prefix}-{index:00000}-of-{split.Count:00000}.gguf"))
            .ToArray();
    }

    /// <summary>The multimodal projector llama-server would load automatically for -hf.</summary>
    public static HubFile? FindBestMmproj(IReadOnlyList<HubFile> files, string modelPath)
    {
        var modelBits = ExtractQuantBits(modelPath);
        var modelDirectories = modelPath.Split('/')[..^1];
        HubFile? best = null;
        int bestDepth = 0, bestDiff = 0;
        foreach (var file in files)
        {
            if (!file.Path.EndsWith(".gguf", StringComparison.Ordinal) ||
                !file.Path.Contains("mmproj", StringComparison.Ordinal)) continue;
            var directories = file.Path.Split('/')[..^1];
            if (directories.Length > modelDirectories.Length ||
                directories.Where((part, i) => part != modelDirectories[i]).Any()) continue;
            var diff = Math.Abs(ExtractQuantBits(file.Path) - modelBits);
            if (best is null || directories.Length > bestDepth ||
                directories.Length == bestDepth && diff < bestDiff)
            {
                best = file;
                bestDepth = directories.Length;
                bestDiff = diff;
            }
        }
        return best;
    }

    public static IReadOnlyList<string> AvailableQuantizations(IReadOnlyList<HubFile> files) =>
        files.Where(file => IsModelFile(file.Path) && IsFirstPart(file.Path))
            .Select(file => GetSplitInfo(file.Path)!.Tag)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Take(40)
            .ToArray();

    // Q4_K_M -> 4, F16 -> 16, BF16 -> 16 (llama.cpp extract_quant_bits).
    public static int ExtractQuantBits(string path)
    {
        var tag = GetSplitInfo(path)?.Tag ?? "";
        var start = tag.IndexOfAny("0123456789".ToCharArray());
        if (start < 0) return 0;
        var end = start;
        while (end < tag.Length && char.IsAsciiDigit(tag[end])) end++;
        return int.TryParse(tag.AsSpan(start, end - start), NumberStyles.None,
            CultureInfo.InvariantCulture, out var bits) ? bits : 0;
    }

    /// <summary>Repository-controlled file names must stay inside the snapshot directory.</summary>
    public static bool IsSafeRelativePath(string path) =>
        path.Length is > 0 and <= 400 &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains('\0') && !path.Contains(':') &&
        path.Split('/').All(segment => segment.Length > 0 && segment is not ("." or ".."));
}

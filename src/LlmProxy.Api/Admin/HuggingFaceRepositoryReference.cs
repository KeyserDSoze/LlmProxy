using System.Text.RegularExpressions;

namespace LlmProxy.Api.Admin;

/// <summary>
/// Turns whatever the operator pastes for a Hugging Face model into a repository id and an optional GGUF quantization:
/// a page URL (<c>https://huggingface.co/org/name</c>, with <c>/tree/main</c>, <c>/blob/...</c> or a query),
/// <c>hf.co/org/name</c>, <c>hf://org/name</c>, or <c>org/name[:quant]</c>.
/// </summary>
public static partial class HuggingFaceRepositoryReference
{
    public sealed record Reference(string Repository, string? Quantization)
    {
        public string ProviderModelName => Quantization is null ? Repository : Repository + ":" + Quantization;
    }

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?:huggingface\.co|hf\.co)/", RegexOptions.IgnoreCase)]
    private static partial Regex HostPrefix();

    public static Reference? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 400) return null;
        var text = input.Trim();
        string? quantization = null;

        if (text.StartsWith("hf://", StringComparison.OrdinalIgnoreCase)) text = text["hf://".Length..];
        else if (HostPrefix().Match(text) is { Success: true } host) text = text[host.Length..];
        else if (text.Contains("://", StringComparison.Ordinal)) return null;

        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0) text = text[..cut];

        // Page URLs carry the quantization as a file name (blob/main/x-Q4_K_M.gguf); repo:QUANT is the explicit form.
        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            quantization = text[(colon + 1)..].Trim('/');
            text = text[..colon];
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var repository = parts[0] + "/" + parts[1];
        if (!HuggingFaceDiscoveryService.ValidRepository(repository)) return null;

        if (quantization is null && parts.Length >= 5 && parts[2] is "blob" or "resolve" &&
            parts[^1].EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            quantization = QuantizationFromFileName(parts[^1]);

        if (quantization is { Length: 0 } || quantization is not null && !Regex.IsMatch(quantization, @"^[A-Za-z0-9_.-]{1,50}$"))
            quantization = null;
        return new Reference(repository, quantization);
    }

    private static string? QuantizationFromFileName(string fileName)
    {
        var match = Regex.Match(fileName, @"(?:^|[-_.])((?:I?Q\d[A-Za-z0-9_]*)|BF16|F16|F32)(?:-\d{5}-of-\d{5})?\.gguf$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}

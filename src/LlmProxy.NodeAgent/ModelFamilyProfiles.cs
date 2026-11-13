using System.Text.RegularExpressions;

namespace LlmProxy.NodeAgent;

/// <summary>What an operator needs to know about one runtime for one model family.</summary>
public sealed record FamilyRecipe(IReadOnlyList<string> Arguments, string? Requirement);

/// <summary>Information about the profile applied to an installation, shown in Admin.</summary>
public sealed record FamilyProfileInfo(
    string Id, string Title, string Source, string VerifiedOn,
    IReadOnlyList<string> AppliedArguments, IReadOnlyList<string> OverriddenByOperator,
    string? Requirement, bool Enabled);

/// <summary>
/// Curated, versioned launch knowledge per model family. A runtime cannot always infer it: llama.cpp reads the
/// chat template, tool format and reasoning style from the GGUF, but vLLM and SGLang need the tool-call and
/// reasoning parsers named explicitly, and llama.cpp ignores the sampling a model card recommends. Hugging Face
/// does not publish these flags in a machine-readable form, so each entry records where it came from and when.
/// Flags are never taken from free text on the Hub: they pass through this allow-list only, because they end up
/// on a docker command line. An unknown model gets no profile and the operator's arguments are used unchanged.
/// </summary>
public sealed record ModelFamilyProfile(
    string Id, string Title, string Source, string VerifiedOn, Regex Pattern,
    IReadOnlyDictionary<string, FamilyRecipe> Recipes)
{
    private static readonly RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static readonly IReadOnlyList<ModelFamilyProfile> All =
    [
        new("muse-glimmer", "Meta Muse Glimmer",
            "https://dev.meta.ai/docs/muse-glimmer/deploy", "2026-11-13",
            new(@"muse[-_ ]?glimmer", Options),
            new Dictionary<string, FamilyRecipe>
            {
                ["vllm"] = new(
                    ["--enable-auto-tool-choice", "--tool-call-parser", "muse_glimmer",
                     "--reasoning-parser", "muse_glimmer", "--generation-config", "auto"],
                    "Needs an image that contains the muse_glimmer parsers (vllm/vllm-openai:muse-glimmer); the generic image may not."),
                ["sglang"] = new(
                    ["--reasoning-parser", "muse", "--tool-call-parser", "muse"],
                    "Needs lmsysorg/sglang:dev-muse-glimmer (multi-arch, includes DGX Spark)."),
                ["llama.cpp"] = new(
                    ["--temp", "1.0", "--top-p", "0.95", "--top-k", "64"],
                    "Needs llama.cpp build b10353 or newer; older builds fail with \"unknown model architecture: 'muse-glimmer'\". " +
                    "Reasoning effort is a chat-template argument (chat_template_kwargs.reasoning_strength), not reasoning_effort.")
            }),
        new("qwen3", "Qwen3 (dense)",
            "https://docs.vllm.ai/en/latest/features/reasoning_outputs.html", "2026-11-13",
            // Dense "Qwen3-<n>B" only: Qwen3-Coder, Qwen3-Next and Qwen3.5+ use different tool parsers.
            new(@"(^|/)qwen3-\d+(\.\d+)?b(\b|-)", Options),
            new Dictionary<string, FamilyRecipe>
            {
                ["vllm"] = new(
                    ["--enable-auto-tool-choice", "--tool-call-parser", "hermes", "--reasoning-parser", "qwen3"],
                    "Needs a vLLM version that provides the qwen3 reasoning parser. Reasoning is returned in message.reasoning " +
                    "(older vLLM: reasoning_content).")
            })
    ];

    public static ModelFamilyProfile? Find(string providerModelName) =>
        All.FirstOrDefault(profile => profile.Pattern.IsMatch(providerModelName ?? string.Empty));

    /// <summary>Flag groups (a flag plus its values) the recipe would add that the operator has not set.</summary>
    public static (IReadOnlyList<string> Added, IReadOnlyList<string> Overridden) Resolve(
        FamilyRecipe recipe, IReadOnlyList<string> operatorArguments)
    {
        var added = new List<string>();
        var overridden = new List<string>();
        foreach (var group in Groups(recipe.Arguments))
        {
            var flag = group[0];
            var present = operatorArguments.Any(arg =>
                arg == flag || arg.StartsWith(flag + "=", StringComparison.Ordinal));
            if (present) overridden.Add(flag);
            else added.AddRange(group);
        }

        return (added, overridden);
    }

    public static IReadOnlyList<string> ArgumentsFor(
        string providerModelName, string runtime, IReadOnlyList<string> operatorArguments, bool enabled)
    {
        if (!enabled || Find(providerModelName) is not { } profile ||
            !profile.Recipes.TryGetValue(runtime, out var recipe))
        {
            return [];
        }

        return Resolve(recipe, operatorArguments).Added;
    }

    public static FamilyProfileInfo? Describe(
        string providerModelName, string runtime, IReadOnlyList<string> operatorArguments, bool enabled)
    {
        if (Find(providerModelName) is not { } profile ||
            !profile.Recipes.TryGetValue(runtime, out var recipe))
        {
            return null;
        }

        var (added, overridden) = Resolve(recipe, operatorArguments);
        return new(profile.Id, profile.Title, profile.Source, profile.VerifiedOn,
            enabled ? added : [], overridden, recipe.Requirement, enabled);
    }

    private static IEnumerable<List<string>> Groups(IReadOnlyList<string> arguments)
    {
        List<string>? current = null;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (current is not null) yield return current;
                current = [argument];
            }
            else current?.Add(argument);
        }

        if (current is not null) yield return current;
    }
}

namespace LlmProxy.Domain.Models;

/// <summary>Limits for the client-supplied logical model name; the persisted columns are 160 characters.</summary>
public static class LogicalModelName
{
    public const int MaxLength = 160;

    public static bool IsAcceptable(string value) => value.Length <= MaxLength;

    /// <summary>Fits a name into the persisted columns so one oversized client value never fails a whole write batch.</summary>
    public static string Truncate(string value) => value.Length <= MaxLength ? value : value[..MaxLength];
}

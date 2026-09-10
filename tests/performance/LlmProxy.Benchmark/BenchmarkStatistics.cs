namespace LlmProxy.Benchmarking;

public static class BenchmarkStatistics
{
    public static double? Percentile(IEnumerable<double> values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (percentile is < 0d or > 1d)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        if (ordered.Length == 1)
        {
            return ordered[0];
        }

        var position = (ordered.Length - 1) * percentile;
        var lowerIndex = (int)Math.Floor(position);
        var upperIndex = (int)Math.Ceiling(position);
        if (lowerIndex == upperIndex)
        {
            return ordered[lowerIndex];
        }

        var fraction = position - lowerIndex;
        return ordered[lowerIndex] + ((ordered[upperIndex] - ordered[lowerIndex]) * fraction);
    }
}

public static string[] Run(string[] values)
{
    return values
        .Select(value => value.Trim())
        .Where(value => 0 < value.Length)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

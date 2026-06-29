namespace CFRezManager;

internal static class TextureReferenceResolver
{
    public static Func<IEnumerable<string>, IReadOnlyList<string>>? Combine(
        params Func<IEnumerable<string>, IReadOnlyList<string>>?[] resolvers)
    {
        Func<IEnumerable<string>, IReadOnlyList<string>>[] activeResolvers = resolvers
            .Where(resolver => resolver is not null)
            .Select(resolver => resolver!)
            .ToArray();

        if (activeResolvers.Length == 0)
        {
            return null;
        }

        if (activeResolvers.Length == 1)
        {
            return activeResolvers[0];
        }

        return names =>
        {
            string[] lookupNames = names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            var results = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Func<IEnumerable<string>, IReadOnlyList<string>> resolver in activeResolvers)
            {
                foreach (string texture in resolver(lookupNames))
                {
                    if (!string.IsNullOrWhiteSpace(texture) && seen.Add(texture))
                    {
                        results.Add(texture);
                    }
                }
            }

            return results;
        };
    }
}

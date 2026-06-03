namespace CodexFlow.Core.Utils;

internal static class CollectionExtensions
{
    public static void AddRange<T>(this ICollection<T> target, IEnumerable<T>? items)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (items == null)
        {
            return;
        }

        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

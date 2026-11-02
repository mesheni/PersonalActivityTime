using System.Collections.ObjectModel;

namespace Pat.App.ViewModels;

internal static class CollectionUpdates
{
    // Keep the collection and unchanged items alive so polling does not reset list scroll/focus.
    public static void Reconcile<T, TKey>(ObservableCollection<T> target, IEnumerable<T> values, Func<T, TKey> key)
        where TKey : notnull
    {
        var next = values.ToList();
        var keys = next.Select(key).ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(key(target[i]))) target.RemoveAt(i);
        for (var i = 0; i < next.Count; i++)
        {
            if (i >= target.Count) target.Add(next[i]);
            else if (!EqualityComparer<TKey>.Default.Equals(key(target[i]), key(next[i])))
            {
                var found = -1;
                for (var j = i + 1; j < target.Count; j++)
                    if (EqualityComparer<TKey>.Default.Equals(key(target[j]), key(next[i]))) { found = j; break; }
                if (found >= 0) target.Move(found, i); else target.Insert(i, next[i]);
            }
            if (!EqualityComparer<T>.Default.Equals(target[i], next[i])) target[i] = next[i];
        }
        while (target.Count > next.Count) target.RemoveAt(target.Count - 1);
    }
}

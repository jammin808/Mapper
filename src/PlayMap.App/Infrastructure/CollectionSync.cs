using System.Collections.ObjectModel;

namespace PlayMap.App.Infrastructure;

/// <summary>Brings an observable list of view models in line with a model list by id: an existing view model
/// is updated in place (so selection, focus and scroll survive a push), a new id gets a new one, a gone id
/// is removed, and the order follows the model. A new view model is made, then updated like any other, so
/// one update path fills every field. A push that changes nothing touches nothing.</summary>
public static class CollectionSync
{
    public static void SyncById<TVm, TModel>(
        this ObservableCollection<TVm> target,
        IReadOnlyList<TModel> source,
        Func<TModel, string> modelId,
        Func<TVm, string> vmId,
        Func<TModel, TVm> create,
        Action<TVm, TModel> update)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in source)
            wanted.Add(modelId(m));
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(vmId(target[i])))
                target.RemoveAt(i);
        }
        for (var i = 0; i < source.Count; i++)
        {
            var m = source[i];
            var id = modelId(m);
            var at = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (string.Equals(vmId(target[j]), id, StringComparison.Ordinal))
                {
                    at = j;
                    break;
                }
            }
            if (at < 0)
            {
                var made = create(m);
                target.Insert(i, made);
                update(made, m);
                continue;
            }
            if (at != i)
                target.Move(at, i);
            update(target[i], m);
        }
    }
}

namespace XFEEliteDangerous.Core;

internal static class NativeIdentityAllocator
{
    /// <summary>Called inside a StateStore update, so legacy allocation is persisted atomically.</summary>
    internal static void Ensure(DatabaseState state)
    {
        var used = new HashSet<uint>();
        foreach (var account in state.Accounts.Values)
            if (account.NativeUserId != 0 && !used.Add(account.NativeUserId))
                throw new InvalidDataException("Duplicate private native user identity.");
        uint candidate = 1;
        foreach (var account in state.Accounts.Values.OrderBy(account => account.Id, StringComparer.Ordinal))
        {
            if (account.NativeUserId != 0) continue;
            while (candidate != 0 && used.Contains(candidate)) candidate++;
            if (candidate == 0) throw new InvalidDataException("Private native identity space is exhausted.");
            account.NativeUserId = candidate;
            used.Add(candidate);
        }
    }
}

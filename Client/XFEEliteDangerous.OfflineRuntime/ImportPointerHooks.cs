using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace XFEEliteDangerous.OfflineRuntime;

/// <summary>Bounded PE32+ import walker. Changes only this image's existing IAT pointers, never system DLL code.</summary>
internal sealed unsafe class ImportPointerHooks
{
    private readonly List<(nint Slot, nint Previous, nint Replacement)> installed = [];
    internal IReadOnlyDictionary<string, nint> Install(nint image, IReadOnlyDictionary<string, nint> replacements)
    {
        var found = Find(image, replacements.Keys.ToHashSet(StringComparer.Ordinal));
        if (replacements.Keys.Any(key => !found.ContainsKey(key))) throw new InvalidDataException("A required main-image import is absent.");
        var previous = new Dictionary<string, nint>(StringComparer.Ordinal);
        try
        {
            foreach (var pair in replacements)
            {
                var slot = found[pair.Key]; var original = *(nint*)slot;
                if (original == 0) throw new InvalidDataException("An imported native pointer is null.");
                Change(slot, pair.Value); installed.Add((slot, original, pair.Value)); previous.Add(pair.Key, original);
            }
            return previous;
        }
        catch { Rollback(); throw; }
    }
    internal void Rollback()
    {
        foreach (var entry in installed.AsEnumerable().Reverse()) if (*(nint*)entry.Slot == entry.Replacement) Change(entry.Slot, entry.Previous);
        installed.Clear();
    }
    private static void Change(nint slot, nint value)
    {
        if (!Native.VirtualProtect(slot, 8, 0x04, out var previous)) throw new InvalidOperationException("Cannot protect owned import slot.");
        try { Interlocked.Exchange(ref Unsafe.AsRef<nint>((void*)slot), value); }
        finally { Native.VirtualProtect(slot, 8, previous, out _); }
    }
    internal static Dictionary<string, nint> Find(nint image, HashSet<string> names)
    {
        if (image == 0 || *(ushort*)image != 0x5a4d) throw new InvalidDataException("Expected a mapped PE image.");
        var pe = *(int*)(image + 0x3c);
        if (pe is < 0x40 or > 0x100000 || *(uint*)(image + pe) != 0x4550 || *(ushort*)(image + pe + 24) != 0x20b)
            throw new InvalidDataException("Expected a mapped PE32+ image.");
        var optional = image + pe + 24; var size = *(uint*)(optional + 56);
        if (size < 4096 || size > 0x70000000) throw new InvalidDataException("Invalid image bounds.");
        var imports = *(uint*)(optional + 120); var importSize = *(uint*)(optional + 124);
        Bound(imports, importSize, size);
        var result = new Dictionary<string, nint>(StringComparer.Ordinal);
        for (uint descriptor = 0; descriptor + 20 <= importSize; descriptor += 20)
        {
            var entry = image + (int)(imports + descriptor); var lookup = *(uint*)entry; var dll = *(uint*)(entry + 12); var iat = *(uint*)(entry + 16);
            if (lookup == 0 && dll == 0 && iat == 0) break;
            if (lookup == 0) throw new InvalidDataException("Original import names are required.");
            Bound(dll, 1, size);
            for (uint index = 0; index < 32768; index++)
            {
                Bound(lookup + index * 8, 8, size); Bound(iat + index * 8, 8, size);
                var thunk = *(ulong*)(image + (int)(lookup + index * 8)); if (thunk == 0) break;
                if ((thunk & 0x8000000000000000) != 0) continue;
                if (thunk > uint.MaxValue) throw new InvalidDataException("Invalid named import.");
                var nameRva = checked((uint)thunk + 2); Bound(nameRva, 1, size);
                var length = 0;
                while (length < 128 && nameRva + length < size && *((byte*)image + nameRva + length) != 0) length++;
                if (length == 128 || nameRva + length >= size) throw new InvalidDataException("Unterminated import name.");
                var name = Marshal.PtrToStringAnsi(image + (int)nameRva, length)!;
                if (names.Contains(name) && !result.TryAdd(name, image + (int)(iat + index * 8))) throw new InvalidDataException("Duplicate required import.");
            }
        }
        return result;
    }
    private static void Bound(uint rva, uint length, uint size)
    { if (rva >= size || (ulong)rva + length > size) throw new InvalidDataException("Import entry exceeds mapped image."); }
}

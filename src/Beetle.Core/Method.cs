using System;

namespace GuiLabs.Dotnet.Recorder;

public class Method : IMemorySpan, IComparable<Method>
{
    public int Token;
    public ulong StartAddress { get; set; }
    public int Size { get; set; }
    public string Namespace;
    public string Name;

    public (int il, int native)[] ILToNativeMap;

    public Module Module;

    public int LookupILOffset(ulong nativeAddress)
    {
        if (ILToNativeMap is not { } map)
        {
            return 0;
        }

        int nativeOffset = (int)(nativeAddress - StartAddress);

        int lo = 0, hi = map.Length - 1;
        int result = -1;

        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (map[mid].native <= nativeOffset)
            {
                result = map[mid].il;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }

    public void SortILToNativeMap()
    {
        if (ILToNativeMap != null)
        {
            Array.Sort(ILToNativeMap, (a, b) => a.native.CompareTo(b.native));
        }
    }

    int IComparable<Method>.CompareTo(Method other)
    {
        int comparison = Token.CompareTo(other.Token);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Name.CompareTo(other.Name);
        return comparison;
    }
}

public struct ILToNativeMap : IEquatable<ILToNativeMap>
{
    private (int, int)[] array;

    public ILToNativeMap()
    {
    }

    public ILToNativeMap((int, int)[] array)
    {
        this.array = array;
    }

    public override bool Equals(object obj)
    {
        if (obj is ILToNativeMap other)
        {
            return Equals(other);
        }

        return false;
    }

    public bool Equals(ILToNativeMap other)
    {
        var left = array;
        var right = other.array;
        if (array == null && right == null)
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i].Item1 != right[i].Item1)
            {
                return false;
            }

            if (left[i].Item2 != right[i].Item2)
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        int hash = 0;

        if (array != null)
        {
            for (int i = 0; i < array.Length; i++)
            {
                hash = (hash, array[i].Item1, array[i].Item2).GetHashCode();
            }
        }

        return hash;
    }
}
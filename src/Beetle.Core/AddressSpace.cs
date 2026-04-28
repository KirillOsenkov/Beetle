using System;
using System.Collections;
using System.Collections.Generic;

namespace GuiLabs.Dotnet.Recorder;

public interface IMemorySpan
{
    public ulong StartAddress { get; }
    public int Size { get; }
}

public class AddressSpace<T> : IReadOnlyList<T>
    where T : class, IMemorySpan, IComparable<T>
{
    private List<(ulong start, ulong end, T payload)> spans = new();

    public int Count => spans.Count;

    public T this[int index]
    {
        get => spans[index].payload;
    }

    public void Add(T span)
    {
        spans.Add((span.StartAddress, span.StartAddress + (ulong)span.Size, span));
    }

    public void Sort()
    {
        spans.Sort((a, b) =>
        {
            int comparison = a.start.CompareTo(b.start);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = a.end.CompareTo(b.end);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = a.payload.CompareTo(b.payload);
            return comparison;
        });
    }

    public T FindSpan(ulong address)
    {
        int lo = 0;
        int hi = spans.Count - 1;

        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            var m = spans[mid];

            if (address < m.start)
            {
                hi = mid - 1;
            }
            else if (address >= m.end)
            {
                lo = mid + 1;
            }
            else
            {
                return m.payload;
            }
        }

        return default;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < spans.Count; i++)
        {
            yield return spans[i].payload;
        }
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

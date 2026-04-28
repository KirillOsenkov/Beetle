using System.Collections.Generic;
using System.Diagnostics;
using CallStackIndex = int;
using CodeAddress = ulong;

namespace GuiLabs.Dotnet.Recorder;

public class CallStacks
{
    private List<List<CallStackIndex>> callees;
    private List<CallStackChain> callStacks;
    private List<CallStackIndex> rootCallees = new();

    public int Count => callStacks == null ? 0 : callStacks.Count;

    public IEnumerable<CallStackChain> EnumerateCallStacks()
    {
        if (callStacks == null)
        {
            yield break;
        }

        for (int i = 0; i < callStacks.Count; i++)
        {
            yield return callStacks[i];
        }
    }

    public CallStackChain this[int index] => callStacks[index];

    public unsafe CallStackIndex GetStackIndexForStackEvent(
        nint addresses,
        int addressCount,
        int pointerSize)
    {
        if (addressCount == 0)
        {
            return -1;
        }

        return (pointerSize == 8) ?
            GetStackIndexForStackEvent64((ulong*)addresses, addressCount, -1) :
            GetStackIndexForStackEvent32((uint*)addresses, addressCount, -1);
    }

    public IReadOnlyList<CodeAddress> GetCallStack(CallStackIndex index)
    {
        if (index <= 0)
        {
            return [];
        }

        List<CodeAddress> result = new();

        while (index >= 0)
        {
            var frame = callStacks[index];
            result.Add(frame.address);
            index = frame.callerIndex;
        }

        return result;
    }

    private unsafe CallStackIndex GetStackIndexForStackEvent32(uint* addresses, int addressCount, CallStackIndex start)
    {
        for (var it = &addresses[addressCount]; it-- != addresses;)
        {
            CodeAddress codeAddress = *it;
            start = InternCallStackIndex(codeAddress, start);
        }

        return start;
    }

    private unsafe CallStackIndex GetStackIndexForStackEvent64(ulong* addresses, int addressCount, CallStackIndex start)
    {
        for (var it = &addresses[addressCount]; it-- != addresses;)
        {
            CodeAddress codeAddress = *it;
            start = InternCallStackIndex(codeAddress, start);
        }

        return start;
    }

    internal CallStackIndex InternCallStackIndex(CodeAddress codeAddressIndex, CallStackIndex callerIndex)
    {
        if (callStacks == null)
        {
            callStacks = new List<CallStackChain>(10000);
            callees = new List<List<CallStackIndex>>(10000);
        }

        int callerInt = (int)callerIndex;

        List<CallStackIndex> frameCallees;
        if (callerInt < 0)
        {
            frameCallees = rootCallees;
        }
        else
        {
            frameCallees = callees[callerInt] ?? (callees[callerInt] = new List<CallStackIndex>());
        }

        // Search backwards, assuming that most recently added is the most likely hit.
        for (int i = frameCallees.Count - 1; i >= 0; --i)
        {
            CallStackIndex calleeIndex = frameCallees[i];
            if (callStacks[calleeIndex].address == codeAddressIndex)
            {
                Debug.Assert(calleeIndex > callerIndex);
                return calleeIndex;
            }
        }

        CallStackIndex ret = callStacks.Count;
        callStacks.Add(new CallStackChain(codeAddressIndex, callerIndex));
        if (frameCallees != null)
        {
            frameCallees.Add(ret);
        }

        callees.Add(null);
        return ret;
    }

    public void AddStack(ulong address, int callerIndex)
    {
        callStacks.Add(new CallStackChain(address, callerIndex));
    }

    public void Initialize(int count)
    {
        callStacks = new List<CallStackChain>(count);
    }

    public struct CallStackChain
    {
        public CallStackChain(CodeAddress address, CallStackIndex callerIndex)
        {
            this.address = address;
            this.callerIndex = callerIndex;
        }

        public CodeAddress address;
        public CallStackIndex callerIndex;
    }
}
using Neo.VM;
using Neo.VM.Types;

namespace Neo.Plugins.FSStorage.morph.invoke
{
    public interface Client
    {
        public bool InvokeFunction(UInt160 contractHash, string method, long fee, params object[] args);
        public InvokeResult InvokeLocalFunction(UInt160 contractHash, string method, params object[] args);
    }
    public class InvokeResult
    {
        public VMState State;
        public long GasConsumed;
        public byte[] Script;
        public StackItem[] ResultStack;
    }
}

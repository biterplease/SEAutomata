using System.Threading;

namespace Automata.Util
{
    public interface IInterlockedDelegate
    {
        int Increment(ref int location);
        int CompareExchange(ref int location1, int value, int comparand);
        int Exchange(ref int location1, int value);
        T Exchange<T>(ref T location1, T value) where T : class;
    }
    public class InterlockedDelegate : IInterlockedDelegate
    {
        public int Increment(ref int location)
        {
            return Interlocked.Add(ref location, 1);
        }
        public int CompareExchange(ref int location1, int value, int comparand)
        {
            return Interlocked.CompareExchange(ref location1, value, comparand);
        }
        public int Exchange(ref int location1, int value)
        {
            return Interlocked.Exchange(ref location1, value);
        }
        public T Exchange<T>(ref T location1, T value) where T : class
        {
            return Interlocked.Exchange(ref location1, value);
        }
    }
}

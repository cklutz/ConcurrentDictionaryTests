using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ConcurrentDictionaryTests
{
    [TestClass]
    public class ConcurrentDictionaryTests
    {
        // --------------------------------------------------------------------------------
        // Immutable Data
        // --------------------------------------------------------------------------------

        class ImmutableData
        {
            internal static AtomicCounter AddCallsCount = new AtomicCounter();

            public ImmutableData(int value)
            {
                Value = value;
            }
            public int Value { get; }
            public ImmutableData Add(ImmutableData other)
            {
                AddCallsCount++;
                return new ImmutableData(Value + other.Value);
            }
            public override string ToString() => $"Value: {Value:N0}";
            public override bool Equals(object obj) => obj is ImmutableData it && it.Value == Value;
            public override int GetHashCode() => Value.GetHashCode();
        }

        [TestMethod]
        public void TestImmutableData_Success()
        {
            var sequential = TestSequential(
                new ConcurrentDictionary<string, ImmutableData>(),
                (dict, i) => dict.AddOrUpdate(Key, _ => new ImmutableData(i), (_, existing) => existing.Add(new ImmutableData(i))));

            long sequentialAddCalls = ImmutableData.AddCallsCount.Value;
            // The expected result here, as with all TestSequential variations, is that we get 9.999 "AddCallsCount".
            // That is, one less than the total loop count (10.000). The first loop adds the entry using the `addValueFactory`,
            // all follow up ones use the `updateValueFactory`.
            Assert.AreEqual(Loops - 1, sequentialAddCalls);
            PrintSummary<ImmutableData>(Console.Out, false, sequential.Value, sequentialAddCalls);
            ImmutableData.AddCallsCount.Reset();

            var concurrent = TestConcurrent(
                new ConcurrentDictionary<string, ImmutableData>(),
                (dict, i) => dict.AddOrUpdate(Key, _ => new ImmutableData(i), (_, existing) => existing.Add(new ImmutableData(i))));

            long concurrentAddCalls = ImmutableData.AddCallsCount.Value;
            PrintSummary<ImmutableData>(Console.Out, true, concurrent.Value, concurrentAddCalls);
            ImmutableData.AddCallsCount.Reset();

            Assert.AreEqual(sequential.Value, concurrent.Value);
            Assert.AreNotEqual(sequentialAddCalls, concurrentAddCalls);
        }

        // --------------------------------------------------------------------------------
        // Mutable Data 
        // --------------------------------------------------------------------------------

        class MutableData
        {
            internal static AtomicCounter AddCallsCount = new AtomicCounter();

            public MutableData(int value)
            {
                Value = value;
            }
            public int Value { get; private set; }
            public MutableData Add(MutableData other)
            {
                AddCallsCount++;
                Value += other.Value;
                return this;
            }
            public override string ToString() => $"Value: {Value:N0}";
            public override bool Equals(object obj) => obj is MutableData it && it.Value == Value;
            public override int GetHashCode() => Value.GetHashCode();
        }

        [TestMethod]
        public void TestMutableData_Fails_Plain()
        {
            var sequential = TestSequential(
                 new ConcurrentDictionary<string, MutableData>(),
                 (dict, i) => dict.AddOrUpdate(Key, _ => new MutableData(i), (_, existing) => existing.Add(new MutableData(i))));

            long sequentialAddCalls = MutableData.AddCallsCount.Value;
            Assert.AreEqual(Loops - 1, sequentialAddCalls);
            PrintSummary<MutableData>(Console.Out, false, sequential.Value, sequentialAddCalls);
            MutableData.AddCallsCount.Reset();

            var concurrent = TestConcurrent(
                new ConcurrentDictionary<string, MutableData>(),
                (dict, i) => dict.AddOrUpdate(Key, _ => new MutableData(i), (_, existing) => existing.Add(new MutableData(i))));

            long concurrentAddCalls = MutableData.AddCallsCount.Value;
            PrintSummary<MutableData>(Console.Out, true, concurrent.Value, concurrentAddCalls);
            MutableData.AddCallsCount.Reset();

            // We will observe more calls to `MutableData.Add()` than with the sequential case.
            // This is due to contention on the `ConcurrentDictionary` instance. Multiple threads,
            // will attempt to `AddOrUpdate()` simultaniously for the same key.
            //

            // Values are not equal because in the concurrent case, the `updateValueFactory` is called potentially multiple times
            // due to multiple threads trying to update simultaneously. Since each "MutableData.Add()" operation updates the
            // internal state of the instance, we essentially perform more "existing = existing + new" than we should, thus
            // resulting in a larger value than in the (correct) sequential case.
            //
            // 

            // Incidentally, the following *could* actually turn out to be equal. But that is very unlikely.
            Assert.AreNotEqual(sequential.Value, concurrent.Value);
            Assert.AreNotEqual(sequentialAddCalls, concurrentAddCalls);
        }

        [TestMethod]
        public void TestMutableData_Fails_With_DefaultLazy()
        {
            var sequential = TestSequential(
                 new ConcurrentDictionary<string, Lazy<MutableData>>(),
                 (dict, i) => dict.AddOrUpdate(Key, _ => new Lazy<MutableData>(() => new MutableData(i)), (_, existing) =>
                 {
                     existing.Value.Add(new MutableData(i));
                     return existing;
                 }));

            long sequentialAddCalls = MutableData.AddCallsCount.Value;
            Assert.AreEqual(Loops - 1, sequentialAddCalls);
            PrintSummary<MutableData>(Console.Out, false, sequential.Value.Value, sequentialAddCalls);
            MutableData.AddCallsCount.Reset();

            var concurrent = TestConcurrent(
                new ConcurrentDictionary<string, Lazy<MutableData>>(),
                (dict, i) => dict.AddOrUpdate(Key, _ => new Lazy<MutableData>(() => new MutableData(i)), (_, existing) =>
                {
                    existing.Value.Add(new MutableData(i));
                    return existing;
                }));

            long concurrentAddCalls = MutableData.AddCallsCount.Value;
            PrintSummary<MutableData>(Console.Out, true, concurrent.Value.Value, concurrentAddCalls);
            MutableData.AddCallsCount.Reset();

            // TODO: Argument ist probably *WRONG*:
            // The Lazy<> trick only works in the context of "GetOrAdd()" but not when using "AddOrUpdate()".
            // The reason is, that the `updateValueFactory` is still called multiple times due to multiple threads
            // calling it simultaneously. Again, since each call to "MutableData.Add()" updates the internal
            // state, we get other values than expected.
            // TODO: Why is the value *smaller* here?
            // In other words: Lazy<> is not a replacement for a lock or mutex when the value already *exists*.
            // It only prevents multiple creations of the value (hence why it works with GetOrAdd()).
            // There must be a way that prevents the `updateValueFactory` from being called multiple times.

            Assert.AreNotEqual(sequential.Value.Value, concurrent.Value.Value);
            Assert.AreEqual(sequentialAddCalls, concurrentAddCalls);
        }

        [TestMethod]
        public void TestMutableData_Fails_With_EqualitySupportLazy()
        {
            var sequential = TestSequential(
                 new ConcurrentDictionary<string, EqualitySupportLazy<MutableData>>(),
                 (dict, i) => dict.AddOrUpdate(Key, _ => new EqualitySupportLazy<MutableData>(() => new MutableData(i)), (_, existing) =>
                 {
                     existing.Value.Add(new MutableData(i));
                     return existing;
                 }));

            long sequentialAddCalls = MutableData.AddCallsCount.Value;
            Assert.AreEqual(Loops - 1, sequentialAddCalls);
            PrintSummary<MutableData>(Console.Out, false, sequential.Value.Value, sequentialAddCalls);
            MutableData.AddCallsCount.Reset();

            var concurrent = TestConcurrent(
                new ConcurrentDictionary<string, EqualitySupportLazy<MutableData>>(),
                (dict, i) => dict.AddOrUpdate(Key, _ => new EqualitySupportLazy<MutableData>(() => new MutableData(i)), (_, existing) =>
                {
                    existing.Value.Add(new MutableData(i));
                    return existing;
                }));

            long concurrentAddCalls = MutableData.AddCallsCount.Value;
            PrintSummary<MutableData>(Console.Out, true, concurrent.Value.Value, concurrentAddCalls);
            MutableData.AddCallsCount.Reset();

            // This does not work for the same reasons that using a plain, mutable `MutableData` instance as the
            // value does not work. We mutate the state of a shared instance due the `updateValueFactory`
            // being called multiple times.
            // The fact that we "nicely" wrap that inside a form of Lazy-instance does not help.

            Assert.AreNotEqual(sequential.Value.Value, concurrent.Value.Value);
            Assert.AreNotEqual(sequentialAddCalls, concurrentAddCalls);
        }

        [TestMethod]
        public void TestMutableData_Explicit_Locking()
        {
            object l = new object();

            var sequential = TestSequential(
                new ConcurrentDictionary<string, MutableData>(),
                (dict, i) => dict.AddOrUpdate(Key, _ => new MutableData(i), (k, existing) =>
                {
                    lock (l)
                    {
                        return existing.Add(new MutableData(i));
                    }
                }));

            long sequentialAddCalls = MutableData.AddCallsCount.Value;
            Assert.AreEqual(Loops - 1, sequentialAddCalls);
            PrintSummary<MutableData>(Console.Out, false, sequential.Value, sequentialAddCalls);
            MutableData.AddCallsCount.Reset();

            var concurrent = TestConcurrent(
                new ConcurrentDictionary<string, MutableData>(),
                (dict, i) => dict.AddOrUpdate(Key, _ => new MutableData(i), (k, existing) =>
                {
                    lock (l)
                    {
                        return existing.Add(new MutableData(i));
                    }
                }));

            long concurrentAddCalls = MutableData.AddCallsCount.Value;
            PrintSummary<MutableData>(Console.Out, true, concurrent.Value, concurrentAddCalls);
            MutableData.AddCallsCount.Reset();

            // Explicit locking also does not work. The lock we acquire *inside* the `updateValueFactory`,
            // of course, does not prevent the it from being called multiple times for a single
            // AddOrUpdate() operation.

            Assert.AreNotEqual(sequential.Value, concurrent.Value);
        }

        // --------------------------------------------------------------------------------
        // Test Infrastructure
        // --------------------------------------------------------------------------------

        class EqualitySupportLazy<T> : Lazy<T>, IEquatable<EqualitySupportLazy<T>>
        {
            public EqualitySupportLazy(Func<T> valueFactory) : base(valueFactory) { }
            public override bool Equals(object obj)
            {
                return obj is EqualitySupportLazy<T> other && IsValueCreated && other.IsValueCreated && other.Value.Equals(Value);
            }
            public bool Equals(EqualitySupportLazy<T> other) => Equals((object)other);
            public override int GetHashCode() => IsValueCreated ? Value.GetHashCode() : 0;
        }

        class AtomicCounter
        {
            private long m_value;
            public long Value => Interlocked.Read(ref m_value);
            public long Increment() => Interlocked.Increment(ref m_value);
            public void Reset() => Interlocked.Exchange(ref m_value, 0);
            public static AtomicCounter operator ++(AtomicCounter counter)
            {
                counter.Increment();
                return counter;
            }
            public override string ToString() => Value.ToString("N0");
        }

        private static void PrintSummary<T>(TextWriter tw, bool concurrent, int value, long addCallsCount)
        {
            tw.Write(typeof(T).Name);
            tw.Write(" ");
            tw.Write(concurrent ? " CONCURRENT " : " SEQUNETIAL ");
            tw.WriteLine();
            tw.Write("\t Value: ");
            tw.Write(value.ToString("N0"));
            tw.Write("\t Add(): #");
            tw.Write(addCallsCount.ToString("N0"));
            tw.WriteLine();
        }

        private const string Key = "key#1";
        private const int TaskCount = 10;
        private const int Loops = 10_000;
        private const int PerTaskLoops = Loops / TaskCount;

        private static T TestSequential<T>(ConcurrentDictionary<string, T> dict, Action<ConcurrentDictionary<string, T>, int> action)
        {
            for (int j = 0; j < TaskCount; j++)
            {
                for (int i = 0; i < PerTaskLoops; i++)
                {
                    action(dict, i);
                }
            }

            return dict[Key];
        }

        private static T TestConcurrent<T>(ConcurrentDictionary<string, T> dict, Action<ConcurrentDictionary<string, T>, int> action)
        {
            var tasks = new Task[TaskCount];
            var barrier = new Barrier(tasks.Length);

            for (int t = 0; t < tasks.Length; t++)
            {
                tasks[t] = Task.Run(() =>
                {
                    // This attempts to increase the concurrency.
                    // Make sure that all Tasks start closer to approximately the same time.
                    // Otherwise, some tasks might already be done when others just started.
                    barrier.SignalAndWait();

                    for (int i = 0; i < PerTaskLoops; i++)
                    {
                        action(dict, i);
                    }
                });
            }

            Task.WaitAll(tasks);

            return dict[Key];
        }
    }
}

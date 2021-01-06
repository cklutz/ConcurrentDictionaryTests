
## Introduction 

It is important to understand the data structures you are using. That sounds like
being obvious, but sometimes it is not as easy as one might think. That does include
things like time complexity ("big O-notation"), general usage guidelines and pros and cons, but also
more subtle things - especially when concurrent usage comes into play. While
missed time complexity and other traits may "only" cost you performance, others
will cause correctness issues that are hard to find.

They say reasoning about correctness in multi-threaded or concurrent scenarios is hard.
And it is, that is why existing and tested data structures that promise simple solutions
are so appealing.

One data structure that is both appealing and, for uninitiated, hideous is the
[`System.Collections.Concurrent.ConcurrentDictionary`](https://docs.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2?view=net-5.0)
class.

It is very much appealing because it looks like it allows simple concurrent use
of a "dictionary" in a multi-threaded context without the need to explicitly lock
access to it.

While this is in general true, there is more to it than meets the eye. As a general
rule of thumb, such data structures protect their _own_ integrity and invariants.
Such is true for `ConcurrentDictionary<>` as well, but they do _not_ protect the
invariants of the data they are holding. In this case the entries of the dictionary,
more specifically the _keys_ and _values_.

In this article I want to explore the [`AddOrUpdate()`](https://docs.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.addorupdate?view=net-5.0) overloads in particular.
There are similar issues here that are already called out upon using `GetOrAdd()`,
and you can find them by binging Google using "ConcurrentDictionary GetOrAdd Lazy".

For the following overload of `AddOrUpdate()` is assumed, the other overloads are similar regarding
the behavior and issues in this article:

```
AddOrUpdate(
    TKey key,
    Func<TKey,TValue> addValueFactory,
    Func<TKey,TValue,TValue> updateValueFactory)
```

Note that for the sake of this article the term "calls `updateValueFactory` method" means
invoking the `updateValueFactory` delegate and thus ultimately the code that it calls.

Let's start by looking into how `AddOrUpdate()` operates [internally](https://source.dot.net/#System.Collections.Concurrent/System/Collections/Concurrent/ConcurrentDictionary.cs,2e5ef5704344a309).
The following steps are performed repeatedly until one succeeds.
There are two cases to consider: (a) the entry already exists, (b) it does not.

1. If the entry does not exist, the equivalent of `TryAdd()` is attempted.

1.1. If that succeeds, `AddOrUpdate()` is complete. 

1.2. If it fails, another thread has concurrently created it and the
     current one loops and then attempts to update the existing entry.

2. If the entry does exist, the current value of it is stored as "oldValue".

2.1. Then `updateValueFactory` is called and the value is stored as "newValue".

2.2. For the key a lock is acquired. This lock is not distinct for each key - that would be
     quite wasteful for large dictionaries - but each key belongs to a bucket and each bucket
     has a lock. That ensures at least some concurrency when actually updating entries, as not
     the complete dictionary is locked, but only "parts" of it (depending on the actual keys
     being accessed simultaneously).

2.3. Holding the lock, the actual entry for the key is looked up (again). Now the value of that
     entry is compared against "oldValue".

2.3.1. If the values do not match (by virtue of
       `EqualityComparer<TValue>.Default`), another thread has concurrently modified the entry,
       and this thread has to retry from the beginning. Note that it is imported to not simply
       restart at 2. or 2.1. Because in the meantime the entry could have been removed by
       another thread, so that case 1. could be the right thing to attempt next.

2.3.2. If the values do match, then no concurrent modification has happened and it is safe to
       replace the current value of the entry (again, which still is "oldValue") with "newValue".
       There are some optimizations about values that are atomic in assignment (e.g `int`), but
       generally the logic is the same.

The key takeaway is the following:

The actual update of the entry's value is done holding a lock, but only so to protect the
internal state of the dictionary instance itself. The `updateValueFactory` method is *not* called
holding this lock. Since the whole procedure could be executed many times for the same key and 
the same `AddOrUpdate()` call site - due to contention of multiple threads executing in parallel,
the `updateValueFactory` method could be called multiple times before the resulting value is
actually inserted (or added) into the dictionary.

This is spelled out in the "Remarks" section of the [`AddOrUpdate()`](https://docs.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.addorupdate?view=net-5.0) documentation:

> If you call AddOrUpdate simultaneously on different threads, addValueFactory may be called multiple times,
> but its key/value pair might not be added to the dictionary for every call.
> For modifications and write operations to the dictionary, ConcurrentDictionary<TKey,TValue> uses fine-grained locking
> to ensure thread safety. (Read operations on the dictionary are performed in a lock-free manner.) However,
> the addValueFactory and updateValueFactory delegates are called outside the locks to avoid the problems that can arise
> from executing unknown code under a lock. Therefore, AddOrUpdate is not atomic with regards to all other operations
> on the ConcurrentDictionary<TKey, TValue> class.

If the code executed by `updateValueFactory` has side effects, that has to be considered.
Or in other words, you cannot rely on the `updateValueFactory` method being called exactly
once for reach `AddOrUpdate()` call.

What about the `addValueFactory`?
As can be seen by the documentation citation above, the `addValueFactory` method could also
be called multiple times for a single `AddOrUpdate()` call. We won't look at this issue here
in particular. For once, the basic problems (immutable, mutable values) are the same as
with the `updateValueFactory` and also because issues there are also explained with the 
various material about `GetOrAdd()` on the Internet.

Another thing to note is that the way of checking to values for equality should be well defined
(see 2.3.1 above), because that is how `ConcurrentDictionary<>` decides if two values are equal.
If you simply use default `Object.Equals()` for a reference type (`class`), then of course only
the exact same instance will count as being equal. Which may or not may be what you want. If you
use a value type as a value, than equality will be defined by "value equality", but for custom
types (`struct`) you should probably still overwrite `Equals()`, and if only for
[performance reasons](https://devblogs.microsoft.com/premier-developer/performance-implications-of-default-struct-equality-in-c/).

This is nothing to be underestimated. It constitutes a fundamental difference regarding
the `System.Collections.Generic.Dictionary<>` class, which only requires equality (and hash code)
being "correct" for keys, not values. We will revisit this in the following, but it is only
aspect.

The code for this article is available on [Github](https://github.com/cklutz/ConcurrentDictionaryTests)
in the form of Unit Tests. To fully understand, you should also look at the output (`Console.Out`) of
each test, as it includes additional information.

So let's delve into details and see how it goes. 

## Immutable Data

Immutable data (structures) and multi threading mix well. There's quite some information on this
topic on the Internet and searching will yield a multitude of useful information. Thus we'll not
go into the theory in detail here.

In the case of `AddOrUpdate()` immutable values in the dictionary are super helpful. It doesn't
matter how often `updateValueFactory` is executed before the returned value is actually inserted
(or updated) into the dictionary, because each call is side-effect free.

A very simple example is this:

```
   public void Test(ConcurrentDictionary<string, int> dictionary)
   {
       dictionary.AddOrUpdate("key", 1, (key, current) => current += 2);
   }
```

The `updateValueFactory` is the lambda expression `(key, current) => current += 2`.
In other words updating the entry's value is the operation of adding `2` to its current
value. Assume that the `Test()` method is called concurrently by multiple threads.

- At the start the dictionary does not contain an entry for "key".
- Thread #1 calls `AddOrUpdate()` and will determine that there is no entry,
  after that its quantum is used, it is prempted and another thread runs.
- Thread #2 calls `AddOrUpdate()` and will also determine that there is no
  entry, thus creating it. Then it is preempted.
  (current entry value: 1)
- Thread #1 resumes and will attempt to add the entry. That fails, because meanwhile
  the entry is already present (added by thread #2). It will thus loop and go into
  the update-entry logic case; determines "oldValue" to be "1".
  It will execute `updateValueFactory` and keep the result as "newValue" on the thread stack.
  Then it is preempted.
  (thread #1: oldValue: 1)
  (thread #1: newValue: 2)
- Thread #3 calls `AddOrUpdate()`, sees that the entry exists, calls `updateValueFactory`
  and also succeeds to update the actual value.
  (current entry value: 2)
- Thread #1 resumes and continues by acquiring the lock for the entry. It then checks
  the current value again and observes that "2" != "1". Thus a concurrent modification
  has happened and it needs to retry from the beginning.
  Upon retry the thread again determines that the entry exists, with value "2",
  it thus calls `updateValueFactory` again, now getting "3" as "newValue".
  (We could introduce more / other threads continuously interrupting each other and
  especially thread #1 in its quest to update the value, but the point should be
  clear by now.)
  Assuming no other thread intervenes or interrupts, the current value is finally
  updated to "3" by thread #1.

In the example above, the `updateValueFactory` method is called _twice_ by thread #1
in the quest to perform _one_ (logical) update. Since each Int32 operation returns a
new value ("instance") this does not matter, as the result of the operation is always
consistent (the second call to `updateValueFactory` yields the correct result of "3").

Immutable in this case basically means that the result of an operation does not change
the internal state of the _existing_ instance, but rather creates a new one. Again,
there is a lot of good material available on immutable data structures (and yes, even
`System.Int32` counts as such along with `System.String`, doesn't have to be too complicated).
so we won't go into more details here. However, recall the above example, for exemplary
purposes, how multiple calls of the `updateValueFactory` delegate could happen for a single
`AddOrUpdate()` call.

Using built-in value types like `Int32` or `String` just works. But what about user defined types.
Well, if you can at all, make them immutable. This will have benefit in many multi-threaded
situations.

For example, consider this type:

```
public class ImmutableData
{
    public ImmutableData(int value)
    {
        Value = value;
    }
    public int Value { get; }
    public ImmutableData Add(ImmutableData other) => new ImmutableData(Value + other.Value);
    public override bool Equals(object obj) => obj is ImmutableData it && it.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}
```

That is of course a rather useless type - we could have easily just used `int` instead.
Technically, there is no need for this wrapper. But then, this class should only serve
as an example for demonstration purposes. Also note, that if this type would indeed be
useful as it is, it should probably not be a `class` but rather a `struct` or even better
a `readonly struct` to emphasis the point of being a value type and an immutable value type.

However, for the workings in conjunction with the `ConcurrentDictionary<>` this makes no
difference, so we simply go with `class`.

Consider the following test. `TestSequential` will run the `AddOrUpdate()` calls sequentially,
whereas `TestConcurrent` will run them using multiple tasks concurrently (for more details checkout
the actual [code](https://github.com/cklutz/ConcurrentDictionaryTests). Obviously, if everything
is correct, the results (the value of the single entry for key `Key` we update), should be
identical, i.e. `sequential.Value == concurrent.Value`.

Both sequential and concurrent runs call the `AddOrUpdate()` method 10.000 times in total.
Note that this code also counts the number of total calls to `ImmutableData.Add()` across all
instances (using the static `ImmutableData.AddCallsCount` member).
This helps understand how often the `updateValueFactory` method is actually called.

```
[TestMethod]
public void TestImmutableData_Success()
{
    var sequential = TestSequential(
        new ConcurrentDictionary<string, ImmutableData>(),
        (dict, i) => dict.AddOrUpdate(Key, _ => new ImmutableData(i), (_, existing) => existing.Add(new ImmutableData(i))));

    long sequentialAddCalls = ImmutableData.AddCallsCount.Value;
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
```

A typical output would yield the following:

```
ImmutableData  SEQUENTIAL 
	 Value: 4.995.000	 Add(): #9.999
ImmutableData  CONCURRENT 
	 Value: 4.995.000	 Add(): #25.383
```

Some observations:

* The resulting values match.
* The concurrent code executed `updateValueFactory` more often than the sequential code
  (in fact, the sequential code executed it exactly 9.999 times, which is one less than
   the total number of `AddOrUpdate()` calls scheduled: 10.000. This is because the
   sequential code calls the `addValueFactory` code exactly once when the entry does
   not exist and then the remaining 10.000 - 1 calls to update the existing value.)
  The results of the "excess" `updateValueFactory` calls have been discarded and didn't
  change the overall result because the actual value of the entry is not affected by them.

## Mutable Data

Things could be wonderful, if there were no mutable data. So in the following let's see
how that goes and would approaches could be attempted and why the ultimately fail.

For all of the following we use the following type:

```
public class MutableData
{
    private int m_value;
    public MutableData(int value)
    {
        m_value = value;
    }
    public int Value => m_value;
    public MutableData Add(MutableData other)
    {
        Interlocked.Add(ref m_value, other.m_value);
        return this;
    }
    public override string ToString() => $"Value: {Value:N0}";
    public override bool Equals(object obj) => obj is MutableData it && it.Value == Value;
    public override int GetHashCode() => Value.GetHashCode();
}
```

In contrast to the immutable variant, this one changes the value held by the current instance,
when performing the `Add()` method, instead of returning a new instance that represents the
new value.

## No special treatment

The first attempt is to simply do nothing special at all. That is, simply treat things as if
the value was immutable.

```
[TestMethod]
public void TestMutableData_Fails_NoSpecialTreatment()
{
    var sequential = TestSequential(
        new ConcurrentDictionary<string, MutableData>(),
        (dict, i) => dict.AddOrUpdate(Key, _ => new MutableData(i), (_, existing) => existing.Add(new MutableData(i))));

    long sequentialAddCalls = MutableData.AddCallsCount.Value;
    PrintSummary<MutableData>(Console.Out, false, sequential.Value, sequentialAddCalls);
    MutableData.AddCallsCount.Reset();

    var concurrent = TestConcurrent(
        new ConcurrentDictionary<string, MutableData>(),
        (dict, i) => dict.AddOrUpdate(Key, _ => new MutableData(i), (_, existing) => existing.Add(new MutableData(i))));

    long concurrentAddCalls = MutableData.AddCallsCount.Value;
    PrintSummary<MutableData>(Console.Out, true, concurrent.Value, concurrentAddCalls);
    MutableData.AddCallsCount.Reset();


    // Incidentally, the following *could* actually turn out to be equal. But that is very unlikely.
    Assert.AreNotEqual(sequential.Value, concurrent.Value);
    Assert.AreNotEqual(sequentialAddCalls, concurrentAddCalls);
}
```

A typical output would yield the following:

```
MutableData  SEQUENTIAL 
	 Value: 4.995.000	 Add(): #9.999
MutableData  CONCURRENT 
	 Value: 4.836.643	 Add(): #10.062
```

Some observations:

* Again, the `updateValueFactory` has been called more times than necessary, which hints at
  concurrent attempts to update the value.
* The actual results are not equal.
 
But shouldn't the concurrent value be _larger_ than the sequential one?
After all the `updateValueFactory` (and thus `MutableData.Add()`) method
is called more often.

The reason for this is because of the way that we treat the first every value
to be inserted.

Each thread, when calling `AddOrUpdate()` for the first time starts with a new instance
of `MutableData`, because `addValueFactory` is written as `_ => new MutableData(i)`.

However, only one of those instances will ultimately be the one that is finally inserted
as the "first" one. All others will be discarded and from then on the `updateValueFactory`
operates on that _single_ instance.

So while we have in fact "shared data" due to the mutability, the threads don't always
operate on it. Only when the first entry is actually present in the dictionary, and
every thread has observed it, it we do.

If we'd written this instead, where we would have exactly _one_ instance of `MutableData`
per dictionary entry ever, things would look different.

```
var value = new MutableData(0);
var concurrent = TestConcurrent(
        new ConcurrentDictionary<string, MutableData>(),
        (dict, i) => dict.AddOrUpdate(Key, _ => value, (_, existing) => existing.Add(new MutableData(i))));
```

The results would show that the result in the concurrent case _is_ actually higher.

That only shows that using mutable data as values is even more involved like only caring for the `updateValueFactory`.

For the purpose of this article - and for symmetry reasons with the other test cases - we leave it with the
variation of `_ => new MutableData(i)`. In the end we seek to demonstrate that the results are different/wrong,
and it doesn't really matter in which way they are. More so, if we would find a way to make using
mutable data correct, it should work in either way anyway.

### The Lazy-Trick

As already mentioned in the introduction, there is this so called "Lazy-Trick" when using the
`GetOrAdd()` method to prevent the `addValueFactory` from being called multiple times.
Then it is (correctly) used as a means to prevent a potentially expensive operation, the
work of the `addValueFactory`, being called multiple, times when only one time would suffice.
More information on this technique can be found [here](https://andrewlock.net/making-getoradd-on-concurrentdictionary-thread-safe-using-lazy/) (amongst other places).

Let's look at another example / test:

```
[TestMethod]
public void TestMutableData_Fails_With_DefaultLazy()
{
    var sequential = TestSequential(
        new ConcurrentDictionary<string, Lazy<MutableData>>(),
        (dict, i) => dict.AddOrUpdate(Key, 
            _ => new Lazy<MutableData>(() => new MutableData(i)),
            (_, existing) =>
            {
                existing.Value.Add(new MutableData(i));
                return new Lazy<MutableData>(existing.Value);
            }));

    long sequentialAddCalls = MutableData.AddCallsCount.Value;
    PrintSummary<MutableData>(Console.Out, false, sequential.Value.Value, sequentialAddCalls);
    MutableData.AddCallsCount.Reset();

    var concurrent = TestConcurrent(
        new ConcurrentDictionary<string, Lazy<MutableData>>(),
        (dict, i) => dict.AddOrUpdate(Key,
            _ => new Lazy<MutableData>(() => new MutableData(i)), (_, existing) =>
            {
                existing.Value.Add(new MutableData(i));
                return new Lazy<MutableData>(existing.Value);
            }));

    long concurrentAddCalls = MutableData.AddCallsCount.Value;
    PrintSummary<MutableData>(Console.Out, true, concurrent.Value.Value, concurrentAddCalls);
    MutableData.AddCallsCount.Reset();

    Assert.AreNotEqual(sequential.Value.Value, concurrent.Value.Value);
    Assert.AreNotEqual(sequentialAddCalls, concurrentAddCalls);
}
```

The output is like this:

```
MutableData  SEQUENTIAL 
	 Value: 4.995.000	 Add(): #9.999
MutableData  CONCURRENT 
	 Value: 14.859.128	 Add(): #32.363
```

Observations:

* The resulting value is not the same as in the sequential case (smaller, for reasons outlined in
  the previous chapter, but regardless it is wrong).
* The number of `updateValueFactory` calls and thus `Add()` methods is different.

Remember that `AddOrUpdate()` uses equality to determine if a value has changed concurrently.
In this case that would be `Lazy<>.Equals()`, which only works on object identity / reference equality.
Since we create a new `Lazy<MutableData>` instance for each call to `addValueFactory` and `updateValueFactory`,
the check inside `AddOrUpdate()` will very often be wrong, and results it much more calls to `updateValueFactory`
upon the retry logic inside `AddOrUpdate()`, since that mutates the actual `MutableData` instance inside,
the value of it increases far beyond the expected value.

Note that the problem would generally be the same, when using other approaches. For example, you could
think about writing the `updateValueFactory` to return the same `Lazy` instance:

```
    var concurrent = TestConcurrent(
        new ConcurrentDictionary<string, Lazy<MutableData>>(),
        (dict, i) => dict.AddOrUpdate(Key,
            _ => new Lazy<MutableData>(() => new MutableData(i)), (_, existing) =>
            {
                existing.Value.Add(new MutableData(i));
                return existing; // <===
            }));
```

That also doesn't work because we are still mutating the single `MutableData` instance; whether that is
reference by the same or different `Lazy` instances is irrelevant.

### Lazy with custom equality

By now it should be clear that this probably leads nowhere, but for the sake of the argument let's see what
happens when we would use a Lazy-type that uses the wrapped object's equality instead. For this we'd first need
something like this:

```
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
```

And some test code:

```
[TestMethod]
public void TestMutableData_Fails_With_EqualitySupportLazy()
{
    var sequential = TestSequential(
        new ConcurrentDictionary<string, EqualitySupportLazy<MutableData>>(),
        (dict, i) => dict.AddOrUpdate(Key,
            _ => new EqualitySupportLazy<MutableData>(() => new MutableData(i)),
            (_, existing) =>
            {
                existing.Value.Add(new MutableData(i));
                return new EqualitySupportLazy<MutableData>(existing.Value);
            }));

    long sequentialAddCalls = MutableData.AddCallsCount.Value;
    PrintSummary<MutableData>(Console.Out, false, sequential.Value.Value, sequentialAddCalls);
    MutableData.AddCallsCount.Reset();

    var concurrent = TestConcurrent(
        new ConcurrentDictionary<string, EqualitySupportLazy<MutableData>>(),
        (dict, i) => dict.AddOrUpdate(Key,
            _ => new EqualitySupportLazy<MutableData>(() => new MutableData(i)),
            (_, existing) =>
            {
                existing.Value.Add(new MutableData(i));
                return new EqualitySupportLazy<MutableData>(existing.Value);
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
```

The output is:

```
MutableData  SEQUENTIAL 
	 Value: 4.995.000	 Add(): #9.999
MutableData  CONCURRENT 
	 Value: 5.017.329	 Add(): #10.073
```

Again, the results don't match and (or because) we have more `updateValueFactory` class then
expected. The reason here is the same as with the plain usage of `Lazy<>`: we simply cannot
account for the fact that a shared `MutableData` instance is changed.

In the end, the whole `Lazy<>` trickery adds nothing to the problem at all.

That does _not_ mean that it doesn't have its place. Again, when trying to prevent to the
unnecessary creation of expensive objects, calling expensive algorithms, etc. It has its
place in conjunction with the `ConcurrentDictionary<>` type, but only in the context of
_initially creating_ a value. May that be during `GetOrAdd()` or the `addValueFactory`
call in the `AddOrUpdate()` case.

### Explicit Locking inside the updateValueFactory

One could attempt to lock the logic inside `updateValueFactory` against multiple concurrent
invocations.

```
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

    Assert.AreNotEqual(sequential.Value, concurrent.Value);
    Assert.AreNotEqual(sequentialAddCalls, concurrentAddCalls);
}
```

The output is:

```
MutableData  SEQUENTIAL 
	 Value: 4.995.000	 Add(): #9.999
MutableData  CONCURRENT 
	 Value: 5.017.839	 Add(): #10.050
```

The result is almost expected by now. Again the concurrent case produces a wrong result and has some
more calls to `updateValueFactory` than required - to rinse and repeat: because multiple threads have
attempted to update the value concurrently and called `updateValueFactory` for each retry.

Why does this not work? Because the lock _inside_ the `updateValueFactory` can not prevent it
from running multiple times for the same `AddOrUpdate()` call. It could protect some sort of
invariant inside the `MutableData` instance, but cannot help otherwise.

### Making it work

For example consider a value of type `List<int>`, obviously that type is mutable. When we add
new values to the list it changes the current instance itself.

```
[TestMethod]
public void TestAdvancedMutableData_Success()
{
    object l = new object();

    var sequential = TestSequential(
        new ConcurrentDictionary<string, List<int>>(),
        (dict, i) => dict.AddOrUpdate(Key,
            _ => new List<int>(),
            (_, existing) =>
            {
                lock (l)
                {
                    if (!existing.Contains(i))
                    {
                        existing.Add(i);
                    }
                    return existing;
                }
            }));

    var concurrent = TestConcurrent(
        new ConcurrentDictionary<string, List<int>>(),
        (dict, i) => dict.AddOrUpdate(Key,
            _ => new List<int>(),
            (_, existing) =>
            {
                lock (l)
                {
                    if (!existing.Contains(i))
                    {
                        existing.Add(i);
                    }
                    return existing;
                }
            }));

    CollectionAssert.AreEquivalent(sequential, concurrent);
}
```

This shows, that using specific measures, calling `updateValueFactory` multiple times can of course 
yields correct results. But only because we actively did something to help it. And that is the key
here: there is no _universal_ approach to make mutable data work in the `AddOrUpdate()` scenario.
You have to find a way specific to your particular case.

And just to emphasis this point, look what happens if we'd used an immutable data type instead of
`List<int>`, `ImmutableList<int>`.

```
[TestMethod]
public void TestAdvancedImmutableData_Success()
{
    object l = new object();

    var sequential = TestSequential(
        new ConcurrentDictionary<string, ImmutableList<int>>(),
        (dict, i) => dict.AddOrUpdate(Key,
            _ => ImmutableList<int>.Empty, 
            (_, existing) => existing.Add(i)));

    var concurrent = TestConcurrent(
        new ConcurrentDictionary<string, ImmutableList<int>>(),
        (dict, i) => dict.AddOrUpdate(Key, 
            _ => ImmutableList<int>.Empty,
            (_, existing) => existing.Add(i)));

    CollectionAssert.AreEquivalent(sequential, concurrent);
}
```

Since `ImmutableList<>.Add()` does not add the new element to existing instance, but creates
a new one, it does not matter how often `updateValueFactory` is called.

## Conclusion

So where does that leave us?

One takeaway is that if you have an immutable data structure or value types as value, you're basically
out of the woods.

If mutable data / types are required, extra precaution is needed to keep things correct. How exactly that
works and if it is feasible at all depends on the actual specific case and there is general solution.
To make things even more interesting, using a `ConcurrentDictionary<>` still works nicely with mutable
values, if you only ever get or add (`ConcurrentDictionary<>[TKey key]`, `GetOrAdd()`, `TryAdd`),
remove (`TryRemove()`) or iterate existing entries, but never update them. Why? Because then you really
treat them as immutable! At least from the point of view of the `ConcurrentDictionary<>`. But this is
of course rather dangerous. A future maintainer might innocently update values (`AddOrUpdate()` or
`TryUpdate()``) values in the dictionary and things start to break.

Considering all the effort and correctness reasoning required for mutable values, and preventing brittle
code for future maintenance - even with immutable values - it might worth considering using a plain
`System.Collections.Generic.Dictionary<>` with explicit locking instead. Then go with that until
measurement and profiling have proven this to be bottleneck for your scenario.

In either case, there are potential issues with performance, since multiple (unnecessary) calls to `updateValueFactory`
or `addValueFactory` can and will happen. Whether that is an issue for your scenario depends on how expensive
these extra calls are and how often they generally will happen. The frequency of such extra calls is
governed by the level of concurrency and contention that may arise from it. As always in such cases there
is no universal answer and you have to measure and profile common use cases to find out. Potential problems
in this regard can still be addressed using value wrapped in `Lazy<>`. As shown above this does not help
with the issues of mutable data, but can reduce or prevent extra (expensive) calls. Wrapping a value into
`Lazy<>` will add memory cost. In the long run each `Lazy<>` instance requires 24 bytes (64 bit; it uses
some more until the value is first requested for the `valueFactory` delegate and some internal state, but
that will be subject to GC when the value has been requested). If you have literally millions of values
in your dictionary that might add up, but again: profile and measure before drawing conclusions here.

As so often in software development there seldom is a universal right choice and such is the case here
as well. This article thus attempted to outline some of the issues involved and give some guidance and
hints. As always YMMV.




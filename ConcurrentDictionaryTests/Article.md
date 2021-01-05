# Introduction 

It is important to understand the data structures you are using. That sounds like
being obvious, but sometimes it is not as easy as one might think. That does include
things like time complexity, general usage guidelines and pros and cons, but also
more subtle things - especially when concurrent usage comes into play. While
the missed time complexity and other traits may "only" cost you performance, others
will cause correctness issues that are hard to find.

One data structure that is both appealing and, for uninitiated, hideous is the
[`System.Collections.Concurrent.ConcurrentDictionary`](https://docs.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2?view=net-5.0)
class.

It is very much appealing because it looks like it allows simple concurrent use
of a "dictionary" in a multi-threaded context without the need to explicitly lock
access to it.

While this is in general true, there is more to it than meets the eye. As usual,
all this potential issues are documented, but in day and age of copy/paste coding,
sometimes forgotten.

In this article we want to explore the [`AddOrUpdate()`](https://docs.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.addorupdate?view=net-5.0) method. We will see similar issues that are already called out upon using `GetOrAdd()`,
and you can find them by binging Google or googling Bing using "ConcurrentDictionary GetOrAdd Lazy",
for example.

Let's start by looking into how `AddOrUpdate()` operates internally (see https://source.dot.net/#System.Collections.Concurrent/System/Collections/Concurrent/ConcurrentDictionary.cs,2e5ef5704344a309, all overloads are equivalent in that respect).

The following steps are performed repeatedly until one succeeds.
There are two cases to consider: (a) the entry already exists, (b) it does not.

1. If the entry does not exist, the equivalent of `TryAdd()` is attempted.
  1.1 If that succeeds, `AddOrUpdate()` is complete. 
  1.2 If it fails, another thread has concurrently created it and the
      current one loops and then attempts to update the existing entry.

2. If the entry does exist, the current value of it is stored as "oldValue".
  2.1 Then the `updateValueFactory` method is called and the value is stored as "newValue".
  2.2 For the key a lock is acquired. This lock is not distinct for each key - that would be
      quite wasteful for large dictionaries - but each key belongs to a bucket and each bucket
      has a lock. That ensures at least some concurrency when actually updating entries, as not
      the complete dictionary is locked, but only "parts" of it (depending on the actual keys
      being accessed simultaneously).
  2.3 Holding the lock, the actual entry for the key is looked up (again). Now the value of that
      entry is compared against "oldValue".
  2.3.1 If the values do not match (by virtue of
        `EqualityComparer<TValue>.Default`), another thread has concurrently modified the entry,
        and this thread has to retry from the beginning. Note that it is imported to not simply
        restart at 2. or 2.1. Because in the meantime the entry could have been removed by
        another thread, so that case 1. could be the right thing to attempt next.
  2.3.2 If the values do match, then no concurrent modification has happened and it is safe to
        replace the current value of the entry (again, which still is "oldValue") with "newValue".
        There are some optimizations about values that are atomic in assignment (e.g `int`), but
        generally the logic is the same.

The key takeaway is the following:

The actual update of the entry's value is done holding a lock, but only so to protect the
internal state of the ConcurrentDictionary. The `updateValueFactory` method is *not* called
holding this lock. Since the whole procedure could be executed many times for the same key and 
the same `AddOrUpdate()` call site - due to contention of mutliple threads executing in parallel,
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

That could scare you away from using `ConcurrentDictionary<>` right there! And frankly, sometimes it should.
Because in some cases, especially when contention on the dictionary is not too high (you need to measure
this for your particular case!), using a regular `Dictionary<>` with an explicit lock might still
be the better case.
But on the other hand, once you understand what the above implies and what it means for your dictionary
values, it is not that hard either.

So let's delve into details and see how it goes.

**What about the `addValueFactory`?**
As can be seen by the documentation citation above, the `addValueFactory` method could also
be called multiple times for a single `AddOrUpdate()` call. We won't look at this issue here
in particular. For once, the basic problems (immutable, mutable values) are the same as
with the `updateValueFactory` and also because issues there are also explained with the 
various material about `GetOrAdd()` on the internet.

# Immutable Data

Immutable data (structures) and multi threading mix well. There's quite some information on this
topic on the internet and searching will yield a multitude of useful information. Thus we'll not
go into the theory in detail here.

In the case of `AddOrUpdate()` immutable values in the dictionary are super helful. It doesn't
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
- Thread #1 resums and continues by acquiring the lock for the entry. It then checks
  the current value again and observes that "2" != "1". Thus a concurrent modification
  has happened and it needs to retry from the beginning.
  Upone retry the thread again determines that the entry exists, with value "2",
  it thus calls `updateValueFactory` again, now getting "3" as "newValue".
  (We could introduce more / other threads continuously interrupting each other and
  especially thread #1 in its quest to update the value, but the point should be
  clear by now.)
  Assuming no other thread interveens or interrupts, the current value is finally
  updated to "3" by thread #1.

In the example above, the `updateValueFactory` method is called _twice_ by thread #1
in the quest to perform _one_ (logical) update. Since each Int32 operation is immutable
this does not matter, as the result of the operation is always consistent (the second
call to `updateValueFactory` yields the correct result of "3").

Immuatable in this case basically means that the result of an operation does not change
the internal state of the _existing_ instance, but rather creates a new one. Again,
there is a lot of good material available on immutable data structures (and yes, even
`System.Int32` counts as such along with `System.String`, doesn't have to be too complicated).
so we won't go into more details here. However, recall the above example, for exemplary
purposes, how multiple calls of the `updateValueFactory` delegate could happen for a single
`AddOrUpdate()` call.



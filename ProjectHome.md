# IMPORTANT #

BookSleeve has been succeeded by [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis), which takes the same concept and performance goals, but which much-better supports things like clusters and fallback. [The full reasons for this decision are here](http://marcgravell.blogspot.com/2014/03/so-i-went-and-wrote-another-redis-client.html).

Future development effort will be in StackExchange.Redis, not BookSleeve.

# Introduction #

By offering pipelined, asynchronous, multiplexed and thread-safe access to redis, BookSleeve enables efficient redis access even for the busiest applications.

# How can I get started? #

The easiest way is via nuget; in VS2010, add a "Library Package Reference"; make sure you are looking at the Online gallery and enter "booksleeve". Then just click "Install" and you should get everything you need:

![http://booksleeve.googlecode.com/hg/nuget.png](http://booksleeve.googlecode.com/hg/nuget.png)

# Why does it exist? #

For full details, see http://marcgravell.blogspot.com/2011/04/async-redis-await-booksleeve.html

Note the API may change a **little** going to 1.0, but is stable enough to drive Stack Exchange...

# How do I use it? #

The entire API is async; if you don't need the result, just queue it up:

```
using (var conn = new RedisConnection("localhost"))
{
    conn.Open();
    conn.Set(12, "foo", "bar");
    ...
```

You can query the result of an operation **as a future**, by:

```
var value = conn.GetString(12, "foo");
// do something else, perhaps some TSQL, while
// that flies over the network and back
string s = conn.Wait(value);
```

Note that the `value` variable here is a `Task<string>`; we could also use the `Task` API to wait (or add a continuation), but the `conn.Wait(value)` approach simplifies timeout-handling (waiting forever is very rarely a good idea), aggregate-exception handling, and obtaining the result value. `Wait` acts as a blocking call.

Alternatively, if you are using the Async CTP, continuations are a breeze:

```
var value = conn.GetString(12, "foo");
...
string s = await value;
```

A connection is thread-safe and (with the exception of `Wait`) non-blocking, so you can share the connection between as many callers as you need - this allows a web-site to make very effective use of just a single redis connection. Additionally, database-switching (the `12` in the examples above) is handled at the message level, so you don't need to issue separate `SELECT` commands - this allows multi-tenancy usage over a **set** of databases without having to synchronize operations.

# But what if something goes badly wrong? How do I see the exceptions? #

If you capture the result and use it in a `Wait` or a continuation, then you'll get the exception **then**. Otherwise the `Task` API exposes the exception on the `TaskScheduler.UnobservedTaskException` event; even if the data is "nice to have", you should handle this event, **do something useful** (like log it to your failure logs), and mark the exception as observed. If you don't do this unobserved exceptions will kill your process. Which sucks, but:

```
TaskScheduler.UnobservedTaskException += (sender, args) =>
{
    Trace.WriteLine(args.Exception,"UnobservedTaskException");
    args.SetObserved();
};
```

(I should note that this is nothing to do with BookSleeve; this is a feature of the `Task` API; if you are doing async work you should be familiar with this already)
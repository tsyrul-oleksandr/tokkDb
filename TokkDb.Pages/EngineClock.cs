namespace TokkDb.Pages;

//V-8 and HS-7. The one place the engine reads the wall clock.
//
//Identifiers take their timestamp from here, an operation's recorded time comes from here, and
//so does the root page's creation time. A test can stand in for a clock that has been set back
//— which real clocks are — and show that identifiers still ascend and that history still says
//what happened in which order. Nothing else may read the clock directly, or that test would
//prove less than it says.
public static class EngineClock {
  private static readonly Func<DateTimeOffset> System = () => DateTimeOffset.UtcNow;
  private static volatile Func<DateTimeOffset> _source = System;

  public static DateTimeOffset Now => _source();

  //Replaces the clock until the returned scope is disposed. For tests only: a stepped-back
  //clock in production is what V-8 protects against, not something to arrange.
  public static IDisposable Override(Func<DateTimeOffset> source) {
    ArgumentNullException.ThrowIfNull(source);
    var previous = _source;
    _source = source;
    return new Restore(previous);
  }

  private sealed class Restore(Func<DateTimeOffset> previous) : IDisposable {
    public void Dispose() {
      _source = previous;
    }
  }
}

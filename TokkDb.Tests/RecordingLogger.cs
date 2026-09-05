using Microsoft.Extensions.Logging;

namespace TokkDb.Tests;

public sealed class RecordingLogger : ILogger {
  public List<string> Messages { get; } = [];

  public IDisposable BeginScope<TState>(TState state) where TState : notnull {
    return null;
  }

  public bool IsEnabled(LogLevel logLevel) {
    return true;
  }

  public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
      Func<TState, Exception, string> formatter) {
    Messages.Add(formatter(state, exception));
  }
}

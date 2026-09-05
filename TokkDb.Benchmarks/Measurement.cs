using System.Globalization;

namespace TokkDb.Benchmarks;

//One number the experimental chapter can quote, with the requirement it answers to.
public record Measurement(
  string Benchmark,
  string Metric,
  double Value,
  string Unit,
  string Requirement = "",
  double? Target = null,
  string Note = "") {

  //Null when the requirement states no number for this metric, so nothing is claimed either way.
  public bool? MeetsTarget => Target is null ? null : Value <= Target;

  //Invariant throughout: a report read on another machine must not depend on that machine's
  //idea of a decimal separator.
  public string FormatValue() {
    return Format(Value);
  }

  public string FormatTarget() {
    return Target is null ? "—" : $"< {Format(Target.Value)} {Unit}";
  }

  private static string Format(double value) {
    return value >= 1000
      ? value.ToString("N0", CultureInfo.InvariantCulture)
      : value.ToString("0.###", CultureInfo.InvariantCulture);
  }

  public string FormatVerdict() {
    return MeetsTarget switch {
      true => "met",
      false => "**missed**",
      _ => "—"
    };
  }
}

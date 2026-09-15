namespace TokkDb.Pages.Query;

//How a query may be answered, as distinct from what it asks (Q-11).
//
//The request is validated against nothing but itself and says nothing about how it will be
//run. These are the caps that bound a run's memory, the crossover that chooses between the two
//In strategies, and — for a test, or for a caller that has measured — a way to force a rule's
//choice. A plan carries the options it was made with, so a plan handed back to Run executes as
//it was explained.
public sealed record QueryOptions {
  public static readonly QueryOptions Default = new();

  private const long Mebibyte = 1024 * 1024;

  //OR-7: what the ordering stage may hold, in the bytes it accounts for — order keys,
  //identities and addresses, never documents — before the query fails rather than the process.
  public long OrderingStageCapBytes { get; init; } = 64 * Mebibyte;

  //RL-6 and Q-9: what one semi-join key set may allocate, measured as the bytes the set itself
  //has allocated rather than the sum of its keys.
  public long KeySetCapBytes { get; init; } = 64 * Mebibyte;

  //RL-3b and Q-13: an In over a projected key set is executed by seeks while the estimated
  //probes — distinct keys times the height of the index — are at most this many times the
  //data pages of the outer collection, and by one membership pass above that.
  //
  //Measured (2026-09-14) over 100 000 records on 953 data pages with a tree of height 3: at 300
  //keys, 900 probes, the seeks and the pass cost the same (243 ms against 283 ms); at 500 keys,
  //1 500 probes, the pass is a third faster (290 ms against 400 ms); at 2 000 keys it is four
  //times faster. The crossover therefore sits where the probes reach the pages, which is 1.0. The
  //report carries both figures on every relation query, so the value can be re-measured rather
  //than trusted.
  public double MembershipCrossover { get; init; } = 1.0;

  //OR-2a, forced. ByRule applies the stated rule.
  public PathChoice PathChoice { get; init; } = PathChoice.ByRule;

  //RL-3b, forced. ByRule applies the crossover.
  public InStrategy InStrategy { get; init; } = InStrategy.ByRule;
}

//OR-2a: which of the two paths an ordered query takes when the predicate's own path does not
//yield the order.
public enum PathChoice {
  ByRule,
  //The predicate's path, ordered afterwards by the ordering stage.
  PredicatePath,
  //A walk of the index that yields the order, with the predicate applied as a filter over it.
  OrderedWalk
}

//RL-3b: how an In over a projected key set is executed.
public enum InStrategy {
  ByRule,
  //One descent of the tree per distinct key.
  Seeks,
  //One pass over the collection, testing each record's key against the set.
  MembershipPass
}

namespace TokkDb.Pages.Query;

//QM-4: what a request can be refused for on its own terms, with no catalogue to consult.
public enum QueryRequestRefusal {
  NegativeSkip,
  NegativeTake,
  SkipWithoutOrder,
  NestedRelationStep,
  RelationStepOutsideConjunction
}

//QM-4a: what a request can be refused for once there is a catalogue to check it against.
public enum QueryPlanRefusal {
  UnknownOrderColumn,
  UnorderableOrderColumn,
  UnknownRelation,
  RelationDirectionMismatch,
  SelfRelationWithoutDirection
}

//Raised when the request is built, before anything is planned: every one of these is visible in
//the request alone, so there is no reason to let it get as far as a catalogue.
public class QueryRequestRefusedException : Exception {
  public QueryRequestRefusedException(QueryRequestRefusal refusal, string message) : base(message) {
    Refusal = refusal;
  }

  public QueryRequestRefusal Refusal { get; }
}

//Raised when the request is planned, which is the first moment a name in it can be checked. Explain
//and Run both plan, so both raise these, and raise them identically.
public class QueryPlanRefusedException : Exception {
  public QueryPlanRefusedException(QueryPlanRefusal refusal, string message) : base(message) {
    Refusal = refusal;
  }

  public QueryPlanRefusal Refusal { get; }
}

//QM-2a: a plan handed back after the catalogue it was made against has changed.
//
//Refused rather than planned again. A caller who held a plan measured a cost, and re-planning
//would return the right records at a cost they did not ask for and cannot see. Planning again is
//one call away for a caller who wants it: the request is on the plan.
public class StalePlanException : Exception {
  public StalePlanException(string collectionName, long expectedVersion, long foundVersion)
    : base($"The plan for {collectionName} was made against catalogue version {expectedVersion}, and the " +
      $"catalogue is now at version {foundVersion}. A stale plan is not re-planned silently; plan its " +
      $"request again to run it against the catalogue as it now is.") {
    CollectionName = collectionName;
    ExpectedVersion = expectedVersion;
    FoundVersion = foundVersion;
  }

  public string CollectionName { get; }
  public long ExpectedVersion { get; }
  public long FoundVersion { get; }
}

//The two structures a query holds in memory, each with a cap of its own (OR-7, RL-6).
public enum QueryCap {
  OrderingStage,
  KeySet
}

//OR-7 and RL-6: one of the two capped structures grew past its cap, and the query failed rather
//than the process. The message carries the three numbers a reader needs: what overran, the size
//it reached and the cap it was held to.
public class QueryCapExceededException : Exception {
  public QueryCapExceededException(QueryCap cap, string subject, long sizeReached, long limit)
    : base(cap == QueryCap.OrderingStage
      ? $"The ordering stage of the query over {subject} reached {sizeReached} bytes of order keys and " +
        $"addresses, over its cap of {limit} bytes (OR-7). Only a deep offset, a page with no Take or a " +
        $"requested total holds every matching record; bound the page, or raise " +
        $"{nameof(QueryOptions.OrderingStageCapBytes)}."
      : $"The key set projected across relation '{subject}' reached {sizeReached} bytes, over its cap of " +
        $"{limit} bytes (RL-6). The figure is what the set allocated, not the sum of its keys; narrow the " +
        $"inner predicate, or raise {nameof(QueryOptions.KeySetCapBytes)}.") {
    Cap = cap;
    Subject = subject;
    SizeReached = sizeReached;
    Limit = limit;
  }

  public QueryCap Cap { get; }

  //The collection of the ordering stage, or the relation of the key set.
  public string Subject { get; }

  public long SizeReached { get; }
  public long Limit { get; }
}

//NF-4a: a query stopped by its cancellation token.
//
//Not an ordinary result with fewer records in it. A caller that catches this holds the partial
//report, because the cost of the abandoned work is the thing worth seeing; a caller that does not
//cannot mistake the page it never received for a short one.
public class QueryCancelledException : OperationCanceledException {
  public QueryCancelledException(QueryReport report, CancellationToken token, Exception inner = null)
    : base($"The query over {report.CollectionName} was cancelled after {report.RecordsExamined} records " +
      $"were examined and {report.PagesRead} pages read; the partial report is on the exception.", inner, token) {
    Report = report;
  }

  //What the query had done when it stopped, with every inner query it had run or started.
  public QueryReport Report { get; }
}

//RL-3a's invariant: no plan reaching the executor carries a relation step. A step's Execute
//throws by design, so a plan with one left behind is not a query evaluated twice, it is a query
//that crashes on its first record — and this names the step before any record is read.
public class QueryPlanInvariantException : InvalidOperationException {
  public QueryPlanInvariantException(string message) : base(message) { }
}

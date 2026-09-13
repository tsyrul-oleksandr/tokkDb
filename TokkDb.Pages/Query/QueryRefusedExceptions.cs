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

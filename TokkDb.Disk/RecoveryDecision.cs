namespace TokkDb.Disk;

public enum RecoveryOutcome {
  //No journal, or one holding nothing: the database was closed with no transaction in flight.
  NothingToRecover = 1,

  //The last transaction reached its commit record, so the database file already holds it.
  CommittedTransactionKept,

  //The journal stops before its images were whole, which means they never became durable and
  //the database file was therefore never touched.
  IncompleteJournalDiscarded,

  //An interrupted transaction was taken back out of the database file.
  UncommittedTransactionRolledBack
}

//What recovery decided and why, so the decision can be logged and asserted on.
public record RecoveryDecision(
  RecoveryOutcome Outcome,
  ulong TransactionId,
  int RestoredPageCount,
  uint TruncatedToPageCount,
  string Reason);

namespace TokkDb.Documents.Values;

//Something a property can be read out of by name.
//
//It exists so that a predicate does not have to know whether the object in front of it was
//parsed into a dictionary or is still lying in the page buffer. Phase 6 evaluates residual
//predicates against the serialized record, and without this the expression tree would need a
//second evaluator to do it — which is the thing DC-5 forbids.
public interface IFieldSource {
  IDocumentValue GetField(string name);
}

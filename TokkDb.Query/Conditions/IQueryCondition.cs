using TokkDb.Values;

namespace TokkDb.Query.Conditions {

	public interface IQueryCondition {
			IValue Match(IValue root, IValue current);
	}
}

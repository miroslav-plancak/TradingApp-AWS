using TradingApp.Infrastructure.Enums;

namespace TradingApp.Infrastructure.Helpers.Retrieval
{
    public static class RetrievalPolicyResolver
    {
        public record RetrievalPolicy(int MaxChunksPerFile);

        private readonly static Dictionary<LlmQueryClassification, RetrievalPolicy> PoliciesMap = new()
        {
            { LlmQueryClassification.NARROW, new RetrievalPolicy(3) },
            { LlmQueryClassification.BROAD, new RetrievalPolicy(5) },
            { LlmQueryClassification.INCONCLUSIVE, new RetrievalPolicy(3) }
        };

        public static int ResolvePolicyValue(LlmQueryClassification routedLlmQueryResponse, Func<RetrievalPolicy, int> propertySelector)
        {
            if (PoliciesMap.TryGetValue(routedLlmQueryResponse, out var policy))
                return propertySelector(policy);

            if (PoliciesMap.TryGetValue(LlmQueryClassification.INCONCLUSIVE, out var defaultValue))
                return propertySelector(defaultValue);

            throw new InvalidOperationException("RetrievalPolicyLookup has no entry for INCONCLUSIVE - this should never happen.");
        }
    }
}

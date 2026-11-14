namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/model_contract_test.go: the provenance contract the raw
/// aggregators report to the orchestrator.
/// </summary>
public sealed class ResponseMetaAccountingTests
{
    [Fact]
    public void ResponseMetaAccountingContract()
    {
        var finalZero = new ResponseMeta
        {
            Usage = new Usage(),
            AccountingEvidence = AccountingEvidence.ExplicitFinal,
        };
        var partialNonzero = new ResponseMeta { Usage = new Usage { TotalTokens = 34 } };
        var cleanCandidate = new ResponseMeta
        {
            AccountingEvidence = AccountingEvidence.CleanEndCandidate,
        };

        Assert.True(finalZero.AccountingFinal(SourceEndReason.Unknown));
        Assert.False(partialNonzero.AccountingFinal(SourceEndReason.Unknown));
        Assert.True(cleanCandidate.AccountingFinal(SourceEndReason.Semantic));
        Assert.False(cleanCandidate.AccountingFinal(SourceEndReason.Error));
    }
}

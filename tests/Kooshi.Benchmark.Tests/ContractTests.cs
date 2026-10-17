using Kooshi.Benchmark.Shared;

namespace Kooshi.Benchmark.Tests;

public class ContractTests
{
    [Fact]
    public void ExecutionResponseCarriesIdentityAndStatus()
    {
        var id = Guid.NewGuid();
        var response = new ExecutionResponse(id, "pending");
        Assert.Equal(id, response.Id);
        Assert.Equal("pending", response.Status);
    }
}

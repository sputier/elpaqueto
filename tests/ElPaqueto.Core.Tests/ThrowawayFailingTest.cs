using Xunit;

namespace ElPaqueto.Core.Tests
{
    public class ThrowawayFailingTest
    {
        [Fact]
        public void AlwaysFails() => Assert.True(false, "Deliberate failure to prove the CI fails.");
    }
}

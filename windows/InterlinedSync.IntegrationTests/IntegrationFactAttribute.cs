using Xunit;

namespace InterlinedSync.IntegrationTests;

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INTERLINEDLIST_EMAIL"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INTERLINEDLIST_PASSWORD"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INTERLINEDLIST_API_BASE_URL")))
        {
            Skip = "Integration test creds not set; skipping live-API test.";
        }
    }
}

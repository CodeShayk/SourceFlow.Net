namespace SourceFlow.Cloud.AWS.Tests.TestHelpers;

/// <summary>
/// xUnit collection definition for AWS integration tests
/// 
/// This collection ensures that all tests marked with [Collection("AWS Integration Tests")]
/// share a single LocalStackTestFixture instance, preventing port conflicts and reducing
/// container startup overhead.
/// 
/// Without this collection definition, xUnit would create separate fixture instances per
/// test class, causing multiple LocalStack containers to attempt binding to port 4566
/// simultaneously, resulting in "port is already allocated" errors.
/// 
/// Usage:
/// [Collection("AWS Integration Tests")]
/// public class MyIntegrationTests { ... }
/// </summary>
[CollectionDefinition("AWS Integration Tests")]
public class AwsIntegrationTestCollection : ICollectionFixture<LocalStackTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}

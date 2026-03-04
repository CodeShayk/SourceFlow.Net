using Azure.Messaging.ServiceBus.Administration;
using FsCheck;
using FsCheck.Xunit;
using SourceFlow.Cloud.Azure.Tests.TestHelpers;

namespace SourceFlow.Cloud.Azure.Tests.Integration;

/// <summary>
/// Property-based tests for Azure test resource management.
/// Feature: azure-cloud-integration-testing
/// </summary>
public class AzureTestResourceManagementPropertyTests
{
    /// <summary>
    /// Property 24: Azure Test Resource Management Completeness
    /// 
    /// For any test execution requiring Azure resources, all resources created during testing 
    /// should be automatically cleaned up after test completion, and resource creation should 
    /// be idempotent to prevent conflicts.
    /// 
    /// **Validates: Requirements 8.2, 8.5**
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public void AzureTestResourceManagementCompleteness_AllCreatedResourcesAreTrackedAndCleanedUp(
        AzureTestResourceSet testResources)
    {
        // Arrange: Create a test environment manager
        var resourceManager = new TestAzureResourceManager();
        var createdResourceIds = new List<string>();

        try
        {
            // Act: Create all resources in the test set
            foreach (var resource in testResources.Resources)
            {
                var resourceId = resourceManager.CreateResource(resource);
                createdResourceIds.Add(resourceId);
            }

            // Assert: All resources should be tracked
            var trackedResources = resourceManager.GetTrackedResources().ToList();
            var allResourcesTracked = createdResourceIds.All(id => trackedResources.Contains(id));

            Assert.True(allResourcesTracked, "Not all created resources are tracked");

            // Assert: Resource creation should be idempotent
            // Creating the same resource again should not create duplicates
            var initialCount = trackedResources.Count;
            foreach (var resource in testResources.Resources)
            {
                resourceManager.CreateResource(resource);
            }
            
            var afterIdempotentCreation = resourceManager.GetTrackedResources().ToList();
            var idempotencyMaintained = afterIdempotentCreation.Count == initialCount;

            Assert.True(idempotencyMaintained, 
                $"Idempotency violated. Initial: {initialCount}, After: {afterIdempotentCreation.Count}");
        }
        finally
        {
            // Cleanup: Ensure all resources are cleaned up
            var cleanupResult = resourceManager.CleanupAllResources();

            // Verify cleanup was complete
            var remainingResources = resourceManager.GetTrackedResources().ToList();
            Assert.Empty(remainingResources);
        }
    }

    /// <summary>
    /// Property 24 (Variant): Resource cleanup should be resilient to partial failures
    /// 
    /// Even if some resources fail to clean up, the cleanup process should continue
    /// and report which resources could not be cleaned up.
    /// </summary>
    [Property(MaxTest = 50, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public void AzureTestResourceCleanup_ResilientToPartialFailures(
        AzureTestResourceSet testResources)
    {
        var resourceManager = new TestAzureResourceManager();
        var createdResourceIds = new List<string>();

        try
        {
            // Create resources
            foreach (var resource in testResources.Resources)
            {
                var resourceId = resourceManager.CreateResource(resource);
                createdResourceIds.Add(resourceId);
            }

            // Simulate a failure scenario by marking some resources as "protected"
            if (createdResourceIds.Count > 1)
            {
                var protectedResource = createdResourceIds[0];
                resourceManager.MarkResourceAsProtected(protectedResource);
            }

            // Attempt cleanup
            var cleanupResult = resourceManager.CleanupAllResources();

            // Should report partial success
            var hasProtectedResources = resourceManager.GetTrackedResources().Any();
            var cleanupReportedIssues = !cleanupResult.Success || cleanupResult.FailedResources.Any();

            Assert.True(!hasProtectedResources || cleanupReportedIssues, 
                "Cleanup did not report protected resources");
        }
        finally
        {
            // Force cleanup of protected resources for test isolation
            resourceManager.ForceCleanupAll();
        }
    }

    /// <summary>
    /// Property 24 (Variant): Resource tracking should survive test environment reinitialization
    /// 
    /// If a test environment is disposed and recreated, it should not leave orphaned resources.
    /// </summary>
    [Property(MaxTest = 50, Arbitrary = new[] { typeof(AzureResourceGenerators) })]
    public void AzureTestResourceTracking_SurvivesEnvironmentReinitialization(
        AzureTestResourceSet testResources)
    {
        var firstManager = new TestAzureResourceManager();
        var createdResourceIds = new List<string>();

        try
        {
            // Create resources with first manager
            foreach (var resource in testResources.Resources)
            {
                var resourceId = firstManager.CreateResource(resource);
                createdResourceIds.Add(resourceId);
            }

            // Get resource state before disposal
            var resourcesBeforeDisposal = firstManager.GetTrackedResources().ToList();

            // Dispose first manager (simulating test environment teardown)
            firstManager.Dispose();

            // Create new manager (simulating test environment reinitialization)
            var secondManager = new TestAzureResourceManager();

            // The new manager should be able to discover existing resources
            // or at minimum, not create conflicts
            var conflictDetected = false;
            foreach (var resource in testResources.Resources)
            {
                try
                {
                    secondManager.CreateResource(resource);
                }
                catch (ResourceConflictException)
                {
                    conflictDetected = true;
                }
            }

            // Either no conflicts (idempotent), or conflicts are properly detected
            var properBehavior = !conflictDetected || 
                               secondManager.CanDetectExistingResources();

            Assert.True(properBehavior, "Resource conflicts not handled properly");
        }
        finally
        {
            firstManager?.ForceCleanupAll();
        }
    }
}

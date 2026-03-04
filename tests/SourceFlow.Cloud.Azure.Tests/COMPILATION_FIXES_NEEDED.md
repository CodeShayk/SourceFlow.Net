# Compilation Fixes Needed for Azure Cloud Integration Tests

## Summary
The test project has 52 compilation errors that need to be fixed before tests can run. This document outlines all required fixes.

## Critical Issues

### 1. Missing IAzureTestEnvironment Interface Reference (Multiple Files)
**Files Affected:**
- `Integration/ManagedIdentityAuthenticationTests.cs`
- `Integration/ServiceBusEventPublishingTests.cs`
- `Integration/ServiceBusSubscriptionFilteringTests.cs`
- `Integration/ServiceBusCommandDispatchingTests.cs`
- `Integration/ServiceBusSubscriptionFilteringPropertyTests.cs`
- `Integration/ServiceBusEventSessionHandlingTests.cs`
- `Integration/KeyVaultEncryptionTests.cs`
- `Integration/KeyVaultEncryptionPropertyTests.cs`

**Problem:** Tests declare `IAzureTestEnvironment?` but the interface exists in the same namespace.

**Solution:** The interface exists at `TestHelpers/IAzureTestEnvironment.cs`. The issue is likely a missing `using` directive or the files need to be recompiled after the interface was added.

**Fix:** Ensure all test files have:
```csharp
using SourceFlow.Cloud.Azure.Tests.TestHelpers;
```

### 2. KeyVaultTestHelpers Constructor Mismatch
**Files Affected:**
- `Integration/KeyVaultEncryptionTests.cs` (line 58)
- `Integration/KeyVaultEncryptionPropertyTests.cs` (line 60)

**Problem:** Constructor requires `(KeyClient, SecretClient, TokenCredential, ILogger)` but tests are calling it incorrectly.

**Current Constructor Signature:**
```csharp
public KeyVaultTestHelpers(
    KeyClient keyClient,
    SecretClient secretClient,
    TokenCredential credential,
    ILogger<KeyVaultTestHelpers> logger)
```

**Fix:** Tests need to create KeyClient and SecretClient before constructing KeyVaultTestHelpers:
```csharp
var credential = await _testEnvironment!.GetAzureCredentialAsync();
var keyVaultUrl = _testEnvironment.GetKeyVaultUrl();
var keyClient = new KeyClient(new Uri(keyVaultUrl), credential);
var secretClient = new SecretClient(new Uri(keyVaultUrl), credential);

_keyVaultHelpers = new KeyVaultTestHelpers(
    keyClient,
    secretClient,
    credential,
    _loggerFactory.CreateLogger<KeyVaultTestHelpers>());
```

### 3. KeyVaultTestHelpers Missing CreateKeyClientAsync Method
**Files Affected:**
- `Integration/KeyVaultEncryptionTests.cs` (lines 85, 119, 149, 196)
- `Integration/KeyVaultEncryptionPropertyTests.cs` (line 65)

**Problem:** Tests call `_keyVaultHelpers.CreateKeyClientAsync()` but this method doesn't exist.

**Solution:** KeyVaultTestHelpers already has a KeyClient injected. Tests should use it directly or add a helper method:
```csharp
// Option 1: Add to KeyVaultTestHelpers
public Task<KeyClient> GetKeyClientAsync() => Task.FromResult(_keyClient);

// Option 2: Modify tests to use the environment's KeyClient directly
var keyVaultUrl = _testEnvironment!.GetKeyVaultUrl();
var credential = await _testEnvironment.GetAzureCredentialAsync();
var keyClient = new KeyClient(new Uri(keyVaultUrl), credential);
```

### 4. Service Bus Session API Issues
**Files Affected:**
- `Integration/ServiceBusEventSessionHandlingTests.cs` (lines 108-109, 254-255, 310-311, 487-488)

**Problem:** Code uses `CreateSessionReceiver` and `ServiceBusSessionReceiverOptions.SessionId` which don't exist in Azure.Messaging.ServiceBus SDK.

**Current (Incorrect) Code:**
```csharp
var receiver = client.CreateSessionReceiver(queueName, new ServiceBusSessionReceiverOptions
{
    SessionId = sessionId
});
```

**Correct API:**
```csharp
var receiver = await client.AcceptSessionAsync(queueName, sessionId);
// or
var receiver = await client.AcceptNextSessionAsync(queueName);
```

**Fix:** Replace all `CreateSessionReceiver` calls with `AcceptSessionAsync`.

### 5. SensitiveDataMasker Missing Methods
**Files Affected:**
- `Integration/KeyVaultEncryptionTests.cs` (lines 241, 270, 291, 292)

**Problem:** Tests call methods that don't exist:
- `MaskSensitiveData(object)`
- `GetSensitiveProperties(Type)`
- `MaskCreditCardNumbers(string)`
- `MaskCVV(string)`

**Solution:** Either:
1. Implement these methods in `SensitiveDataMasker` class
2. Remove these tests (they test functionality that doesn't exist in the actual codebase)
3. Mock the `SensitiveDataMasker` for testing purposes

**Recommended:** Remove these tests as they test non-existent functionality. The actual `SensitiveDataMasker` in `SourceFlow.Cloud.Core` may have different methods.

### 6. FsCheck Property Test Syntax Issues
**Files Affected:**
- `Integration/KeyVaultEncryptionPropertyTests.cs` (lines 88, 136, 183, 225, 269)
- `Integration/ServiceBusSubscriptionFilteringPropertyTests.cs` (lines 93, 160, 226, 292)

**Problem:** `Prop.ForAll` type arguments cannot be inferred.

**Current (Incorrect) Code:**
```csharp
Prop.ForAll(generator, testFunction).QuickCheckThrowOnFailure();
```

**Fix:** Explicitly specify type arguments:
```csharp
Prop.ForAll<TInput>(generator, testFunction).QuickCheckThrowOnFailure();
```

### 7. Random Ambiguity
**File Affected:**
- `TestHelpers/AzureResourceGenerators.cs` (line 173)

**Problem:** `Random` is ambiguous between `FsCheck.Random` and `System.Random`.

**Fix:** Use fully qualified name:
```csharp
var random = new System.Random();
```

### 8. ManagedIdentityAuthenticationTests Task Type Mismatch
**File Affected:**
- `Integration/ManagedIdentityAuthenticationTests.cs` (line 262)

**Problem:** Cannot convert `List<ValueTask<AccessToken>>` to `IEnumerable<Task>`.

**Fix:** Convert ValueTask to Task:
```csharp
await Task.WhenAll(tokenTasks.Select(vt => vt.AsTask()));
```

## Recommended Approach

Given the scope of errors, I recommend:

1. **Fix infrastructure issues first** (IAzureTestEnvironment, KeyVaultTestHelpers constructor)
2. **Fix Service Bus API issues** (session receiver calls)
3. **Remove or fix SensitiveDataMasker tests** (test non-existent functionality)
4. **Fix FsCheck syntax** (add explicit type parameters)
5. **Fix minor issues** (Random ambiguity, Task conversion)

## Estimated Effort

- **High Priority Fixes** (1-2): ~30 minutes
- **Medium Priority Fixes** (3-4): ~45 minutes  
- **Low Priority Fixes** (5-8): ~30 minutes

**Total**: ~1.5-2 hours of focused development time

## Next Steps

1. Start with KeyVaultEncryptionTests.cs - fix constructor and remove SensitiveDataMasker tests
2. Fix ServiceBusEventSessionHandlingTests.cs - update to correct Service Bus API
3. Fix property test syntax in all affected files
4. Build and verify compilation
5. Run tests to identify runtime issues

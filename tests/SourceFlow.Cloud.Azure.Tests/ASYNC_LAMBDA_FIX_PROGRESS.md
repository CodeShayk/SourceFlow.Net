# Async Lambda Fix Progress

## Summary
Fixing FsCheck property tests that use async lambdas, which are not supported by FsCheck's `Prop.ForAll`.

## Pattern Applied
```csharp
// BEFORE (doesn't compile)
return Prop.ForAll(async (input) => {
    await SomeAsyncOperation();
    return true;
});

// AFTER (compiles)
return Prop.ForAll((input) => {
    SomeAsyncOperation().GetAwaiter().GetResult();
    return true;
});
```

## Files Completed ✅

### 1. KeyVaultEncryptionPropertyTests.cs
- Fixed 5 async property tests
- Added explicit type parameters `Prop.ForAll<string>(...)`
- All methods converted to synchronous wrappers

### 2. ServiceBusSubscriptionFilteringPropertyTests.cs
- Fixed 4 async property tests
- Added explicit type parameters for custom types
- All methods converted to synchronous wrappers

### 3. AzureAutoScalingPropertyTests.cs
- Fixed 10 async property tests
- All methods converted to synchronous wrappers

## Files Remaining ❌

### 4. AzureConcurrentProcessingPropertyTests.cs
**Estimated**: ~8 async property tests
**Lines with errors**: 78, 110, 129, 161, 178, 218, 235, 283, 312, 333, 360, 379, 405, 422, 449, 468, 497

### 5. AzurePerformanceMeasurementPropertyTests.cs
**Estimated**: ~7 async property tests
**Lines with errors**: 76, 112, 129, 167, 184, 222, 236, 275, 294, 319, 336, 370, 385

### 6. AzureHealthCheckPropertyTests.cs
**Estimated**: ~6 async property tests
**Lines with errors**: 205, 245, 250, 322, 362, 367, 380, 401, 406, 419, 440, 445, 458, 478, 483, 496, 514, 519

### 7. AzureTelemetryCollectionPropertyTests.cs
**Estimated**: ~6 async property tests
**Lines with errors**: 209, 251, 256, 340, 374, 379, 392, 431, 436, 449, 483, 488, 501, 540, 545

## Error Types Remaining

### CS4010: Cannot convert async lambda
```
Cannot convert async lambda expression to delegate type 'Func<MessageSize, bool>'.
An async lambda expression may return void, Task or Task<T>, none of which are convertible to 'Func<MessageSize, bool>'.
```

### CS8030: Anonymous function converted to void returning delegate
```
Anonymous function converted to a void returning delegate cannot return a value
```

### CS0411: Type arguments cannot be inferred
```
The type arguments for method 'Prop.ForAll<Value, Testable>(Arbitrary<Value>, FSharpFunc<Value, Testable>)' 
cannot be inferred from the usage. Try specifying the type arguments explicitly.
```

## Next Steps

1. Fix AzureConcurrentProcessingPropertyTests.cs (~8 methods)
2. Fix AzurePerformanceMeasurementPropertyTests.cs (~7 methods)
3. Fix AzureHealthCheckPropertyTests.cs (~6 methods)
4. Fix AzureTelemetryCollectionPropertyTests.cs (~6 methods)
5. Run full build to verify all errors resolved
6. Run tests to identify any runtime issues

## Estimated Remaining Effort
- **Time**: 2-3 hours
- **Methods to fix**: ~27 async property tests
- **Pattern**: Consistent across all files (remove async, add .GetAwaiter().GetResult())

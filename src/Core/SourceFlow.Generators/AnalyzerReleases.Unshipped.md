### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SF0001 | SourceFlow.Messages | Error | Message type has no [MessageType]
SF0002 | SourceFlow.Messages | Error | Duplicate message type name and version
SF0003 | SourceFlow.Commands | Error | Command target id is missing or ambiguous
SF0004 | SourceFlow.Purity | Error | When method has side effects
SF0005 | SourceFlow.Shape | Warning | Event produced by Decide has no When
SF0006 | SourceFlow.Commands | Warning | Command has no Decide or handler
SF0007 | SourceFlow.Purity | Error | Decide must be synchronous
SF0008 | SourceFlow.Shape | Error | Aggregate or process manager shape
SF0009 | SourceFlow.Purity | Warning | Non-deterministic API in a pure method
SF0010 | SourceFlow.Serialization | Error | Type missing from the module's JSON context
SF0011 | SourceFlow.Security | Error | Personal data has no resolvable subject
SF0013 | SourceFlow.Security | Warning | AsIs() publishes personal data
SF0014 | SourceFlow.Commands | Error | Command is both owned and routed
SF0015 | SourceFlow.Query | Error | The query side is used in write-model code
SF0016 | SourceFlow.Query | Warning | A command handler reads through the query side
SF0017 | SourceFlow.Security | Warning | [Queryable] on personal data has no effect
SF0018 | SourceFlow.Query | Error | Domain query is not valid
SF0019 | SourceFlow.Manifest | Warning | Decide outcome can't be determined

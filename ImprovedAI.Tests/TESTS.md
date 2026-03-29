# Test inventory (ImprovedAI.Tests)

## Virtual network / messaging (in scope)

| Area | File(s) |
|------|---------|
| MessageQueue subscribe, broadcast, direct, filters, ack, DLQ, TTL | `VirtualNetwork/MessageQueueTests.cs` |
| Scheduler logistics merge helper | `VirtualNetwork/SchedulerLogisticsUpdateMergeTests.cs` |
| Drone task-assignment guards / enqueue | `VirtualNetwork/TaskAssignmentMessageHandlingTests.cs` |
| LC payload protobuf round-trip | `VirtualNetwork/LogisticsPayloadSerializationTests.cs` |
| Ack behaviour notes | `VirtualNetwork/README_ACKS.md` |
| Message ID util | `Util/IdGeneratorTests.cs` |

## Pathfinding (out of scope for messaging plan)

| Area | File(s) |
|------|---------|
| Pathfinding, direct pathfinder, A*, context | `Pathfinding/*` — keep as regression only; do not require for messaging work |

## Obsolete / ignored

| Area | Reason |
|------|--------|
| `IAIDroneControllerRotationTests` | Entire class `[Ignore]` — pre-existing rotation failures after test harness fixes; replace with messaging-focused drone tests when appropriate |

## Test configuration

- `FakeMessageQueueConfig` — mutable `IMessageQueueConfig` for headless MessageQueue tests.
- `ProtoBufMyUtilitiesDelegate` — protobuf-net round-trip without `MyAPIGateway`.

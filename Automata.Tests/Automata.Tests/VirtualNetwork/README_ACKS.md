# Virtual network acknowledgements

- **`RequiresAck` on send**: `MessageQueue.EnqueueMessage` records `(senderId, messageId)` in `_pendingAcknowledgments`.
- **Clear on successful read**: When a subscriber successfully deserializes a message in `ReadMessages`, `AckMessage(senderId, messageId)` removes that pending entry.
- **Expiry**: If a message expires before delivery and `RequiresAck` is true, it is routed to `DEAD_LETTER_QUEUE` and the pending ack entry is dropped (`ReturnUnackedMessage`).
- **Current product defaults**: Drone reports and LC outbound traffic use `RequiresAck = false` today; schedulers/tests can set `true` for delegated work.

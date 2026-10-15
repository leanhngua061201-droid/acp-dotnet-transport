# Changelog

## 0.1.0

- Initial standalone source preview with a .NET 8 library, console checks, local duplex example, MIT license, and CI workflow.
- Fault the connection if cancellation interrupts a notification frame after acquiring the write lock. Cancellation while waiting for the lock leaves the transport usable.
- Bound late-response bookkeeping by queue length as well as retained IDs, and synchronize pending-request removal with abandoned-ID registration and response dispatch.
- Add regressions for both cancellation cases and 1100 cancelled requests followed by late replies.
- Preserve blocked-send timeout errors after a partial-write cancellation faults the transport, while retaining caller cancellation semantics; add both regressions.
- Write complete UTF-8 byte frames without a buffered writer; track active sends and handlers, share disposal completion, and drain work before releasing synchronization resources.
- Keep throwing application cancellation callbacks from bypassing cleanup; report their error after shutdown finishes.
- Expand the default suite to 20 checks, including shutdown races, 10,000 correlated requests with fragmented concurrent writes, and 2,056 cancelled/timed-out late responses.
- Add an opt-in timed soak check and a process-based real-agent smoke client. Verify initialization, session creation, and an exact-sentinel text reply with Claude Agent ACP 0.90.0 and Claude CLI 2.1.210 using GLM through an Anthropic-compatible backend; leave real-agent tools, cancellation, and resume unverified.
- Require an exact sentinel reply in the smoke client, and continue all cleanup steps even if one fails; report only exception types for external-agent errors.

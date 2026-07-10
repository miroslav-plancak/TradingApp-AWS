# TradingApp

An event-driven order processing system built with ASP.NET Core, Azure Functions (.NET 8 isolated worker), Azure Service Bus, SQL Server, Azure Key Vault, and Application Insights.

---

## Architecture Overview

```
POST /api/orders
       │
       ▼
TradingApp.API ──► SQL Server (atomic Order + OutboxMessage via transaction)
                       │
                       ▼
       ScheduledOutboxMessageProcessor (TimerTrigger, 1 min)
         Phase 1: QuarantineExhaustedMessages()
         Phase 2: IsServiceBusReachableAsync() check
                  └─ ProcessPendingMessages() with Circuit Breaker
         Phase 3: AutoRecoverResurrectedMessages()
                       │
                       ▼
              CREATE_ORDER_QUEUE (Azure Service Bus)
                       │
                       ▼
          OrderExecutionProvider (ServiceBusTrigger)
          Idempotent via ExecuteUpdateAsync(!IsProcessed)
          Randomly assigns ACKNOWLEDGED or REJECTED
                       │
                       ▼
              order_events_topic (Azure Service Bus)
              Subject: "OrderProcessed" | Sequence: 1
              SessionId: ClientOrderId
              ├──► notifications subscription (sessions enabled)
              ├──► risk-analysis subscription
              └──► audit-log subscription
                       │
       ScheduledOrderStatusProcessor (TimerTrigger, 1 min)
       Promotes ACKNOWLEDGED → FILLED
       Publishes to topic: Subject "OrderStatusFilled" | Sequence: 2
       Falls back to UnpublishedTopicMessages on failure
                       │
                       ▼
       ScheduledUnpublishedTopicMessagesProcessor (TimerTrigger, 1 min)
       Retries failed topic publishes with Circuit Breaker
```

---

## Order Status Lifecycle

```
PENDING_ACK (0) ──► ACKNOWLEDGED (1) ──► FILLED (3)
                └─► REJECTED (2)
```

---

## Azure Functions

| Function | Trigger | Responsibility |
|---|---|---|
| `OrderExecutionProvider` | ServiceBus: `CREATE_ORDER_QUEUE` | Processes orders idempotently, publishes `OrderProcessed` event (Sequence 1) to topic |
| `ScheduledOutboxMessageProcessor` | Timer: every 1 min | 3-phase: quarantine exhausted → check SB health → dispatch pending → auto-recover quarantined |
| `ScheduledUnpublishedTopicMessagesProcessor` | Timer: every 1 min | Retries failed topic publishes from `UnpublishedTopicMessages` table with Circuit Breaker |
| `ScheduledOrderStatusProcessor` | Timer: every 1 min | Promotes ACKNOWLEDGED → FILLED, publishes `OrderStatusFilled` event (Sequence 2) to topic |
| `DeadLetterQueueProcessor` | ServiceBus: `CREATE_ORDER_QUEUE/$DeadLetterQueue` | Persists dead-lettered messages to `DeadLetterLogs` |
| `NotificationsProcessor` | ServiceBus Topic: `notifications` | Sequence-ordered delivery with Teams webhook integration |
| `RiskAnalysisProcessor` | ServiceBus Topic: `risk-analysis` | Receives `OrderProcessed` events for risk analysis |
| `AuditLogProcessor` | ServiceBus Topic: `audit-log` | Receives `OrderProcessed` events for audit logging |

---

## Database Tables

| Table | Purpose |
|---|---|
| `Orders` | Core order records. `ClientOrderId` is the unique business key. `IsProcessed` guards idempotency. |
| `OutboxMessages` | Transactional outbox. Written atomically with `Orders`. Dispatched to Service Bus queue by timer. |
| `QuarantinedOutboxMessages` | Messages that exhausted 5 retries. Auto-resurrected when Service Bus recovers. |
| `UnpublishedTopicMessages` | Failed topic publishes from `OrderExecutionProvider` and `ScheduledOrderStatusProcessor`. Retried by dedicated timer. |
| `DeadLetterLogs` | Persisted dead-letter messages from the queue DLQ. |
| `OrderNotificationSequences` | Tracks last processed sequence number per order in `NotificationsProcessor` to enforce ACKNOWLEDGED before FILLED ordering. |
| `PendingFilledNotifications` | Persists out-of-order FILLED events to the database so they survive process restarts. Retrieved and sent after ACKNOWLEDGED is processed. |

---

## Reliability Patterns

### Transactional Outbox

`POST /api/orders` atomically inserts an `Order` and an `OutboxMessage` in a single SQL transaction. If the transaction fails, neither record exists. The outbox is the only path to Service Bus — no direct publishing from the API.

### Idempotent Processing

`OrderExecutionProvider` uses `ExecuteUpdateAsync` with a `WHERE !IsProcessed` clause. If a duplicate message arrives, zero rows are updated and processing exits cleanly. A pre-flight check in `ScheduledOutboxMessageProcessor` also guards against re-dispatching already-processed orders.

### Circuit Breaker (Polly)

Both `ScheduledOutboxMessageProcessor` and `ScheduledUnpublishedTopicMessagesProcessor` wrap their Service Bus send calls in a Polly `AsyncCircuitBreakerPolicy` registered as a singleton. After 3 consecutive failures the circuit opens for 2 minutes, failing immediately without network calls. Configured via `AddServiceBusCircuitBreaker()` extension in each function's `Extensions/ServiceCollectionExtensions.cs`.

### Service Bus Pre-flight Check

`ScheduledOutboxMessageProcessor` calls `IsServiceBusReachableAsync()` before processing each batch. This uses `ServiceBusAdministrationClient.GetQueueRuntimePropertiesAsync()` — a lightweight management API call that does not consume messages. If unreachable, `ProcessPendingMessages()` and `AutoRecoverResurrectedMessages()` are skipped entirely for that cycle.

### Message Quarantine and Auto-Recovery

Messages that fail 5 times move from `OutboxMessages` to `QuarantinedOutboxMessages` with reason `ServiceBusUnavailable`. When Service Bus recovers and `IsServiceBusReachableAsync()` returns true, `AutoRecoverResurrectedMessages()` resets their retry count to 4 and clears `ProcessedAt`, allowing them to re-enter the dispatch pipeline on the next cycle.

### Unpublished Topic Message Fallback

When `OrderExecutionProvider` or `ScheduledOrderStatusProcessor` cannot publish to `order_events_topic`, the event is saved to `UnpublishedTopicMessages` with the original `OrderStatus`. `ScheduledUnpublishedTopicMessagesProcessor` retries up to 5 times with Circuit Breaker protection. The stored `OrderStatus` is used on retry rather than re-querying the `Orders` table to preserve the status at the time of the original failure.

### Sequence-Ordered Notification Delivery

`NotificationsProcessor` uses Service Bus sessions (`SessionId = ClientOrderId`) combined with a sequence number on the `OrderStatusEvent` payload to guarantee ACKNOWLEDGED is always delivered to downstream consumers before FILLED, regardless of which publisher wins the race to the subscription.

- `OrderExecutionProvider` publishes with `Sequence = 1`
- `ScheduledOrderStatusProcessor` publishes with `Sequence = 2`
- If FILLED arrives before ACKNOWLEDGED, it is serialized and persisted to `PendingFilledNotifications` in the database (not memory, so it survives process restarts)
- When ACKNOWLEDGED arrives and is processed, the function queries `PendingFilledNotifications`, processes FILLED immediately in the correct order, and cleans up both tracking tables

Sessions guarantee that messages for the same order are processed one at a time, preventing concurrent access to the sequence tracking state.

### Teams Webhook Notifications

`NotificationsProcessor` sends a real-time Microsoft Teams message card for every order status change via an Incoming Webhook. The card includes Order ID, Status, Processed At timestamp, and Correlation ID for direct Application Insights trace lookup. The webhook URL is read from `TeamsWebhookUrl` in configuration.

---

## Distributed Tracing

Every component propagates `CorrelationId` end to end:

- The API generates `CorrelationId` via `Activity.Current?.TraceId.ToString()`, linking it to the Application Insights `operation_Id`
- Service Bus queue messages carry `CorrelationId` in message metadata (`message.CorrelationId`)
- The `OrderStatusEvent` payload carries `CorrelationId` directly on the event object — all topic subscribers read it from the payload rather than from Service Bus message metadata
- All function logs include `CorrelationId` as a structured property, queryable in Application Insights via `customDimensions.CorrelationId`

---

## Event Contract

All topic publishers and subscribers use the unified `OrderStatusEvent`:

```csharp
public class OrderStatusEvent
{
    public Guid ClientOrderId { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset EventTime { get; set; }
    public int Sequence { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}
```

Queue messages to `CREATE_ORDER_QUEUE` use `OrderPayload` (containing only `ClientOrderId`). `OrderStatusEvent` is exclusively used for topic messages.

---

## Shared Contract Package (Azure Artifacts)

`TradingApp.Events` is distributed as a versioned NuGet package rather than a project reference, so that changing the contract is a deliberate, per-consumer opt-in rather than an instant change across every function.

- **Feed:** `fintech-packages`, project-scoped feed in the `fintech-eda-sandbox` Azure DevOps org (project `FintechTradingAppDemo`)
- **Current version:** `1.0.0`
- **Consumers (`PackageReference`):** `OrderExecutionProvider`, `DeadLetterQueueProcessor`, `NotificationProcessor`, `RiskAnalysisProcessor`, `AuditLogProcessor`, `ScheduledOrderStatusProcessor`, `ScheduledUnpublishedTopicMessagesProcessor` — the seven functions that actually touch `OrderStatusEvent`/`OrderPayload`
- **Not a consumer:** `TradingApp.API`, `TradingApp.Business`, `TradingApp.Domain`, `ScheduledOutboxMessageProcessor` — none of these reference the `TradingApp.Events` namespace

Each consuming project has its own `nuget.config` declaring the feed as a package source:

```xml
<configuration>
  <packageSources>
    <add key="azure-artifacts" value="https://pkgs.dev.azure.com/fintech-eda-sandbox/FintechTradingAppDemo/_packaging/fintech-packages/nuget/v3/index.json" />
  </packageSources>
</configuration>
```

This only declares *where* the feed is — restoring still requires the developer's own Azure DevOps credentials (PAT with `Packaging: Read & Write`, or interactive sign-in via the Azure Artifacts Credential Provider) to actually authenticate against it.

### Publishing a new version

```
dotnet pack TradingApp.Events/TradingApp.Events.csproj -c Release -o ./artifacts
dotnet nuget push --source azure-artifacts --api-key az ./artifacts/TradingApp.Events.<version>.nupkg
```

Version bumps follow SemVer: **PATCH** for internal fixes with no shape change, **MINOR** for additive/backward-compatible fields, **MAJOR** for breaking changes (renamed/removed fields, changed types) — the signal for every consumer to stop and check compatibility before upgrading their `PackageReference` version.

Because the package ships a Release build, Visual Studio's "Just My Code" will warn when debugging into it and won't reliably hit breakpoints inside it — expected, since it's plain DTOs with no logic to step through.

---

## Service Bus Configuration

| Resource | Type | Sessions |
|---|---|---|
| `CREATE_ORDER_QUEUE` | Queue | No |
| `order_events_topic` | Topic | — |
| `notifications` subscription | Topic subscription | **Yes — required** |
| `risk-analysis` subscription | Topic subscription | No |
| `audit-log` subscription | Topic subscription | No |

The `notifications` subscription must have sessions enabled. Sessions cannot be added to an existing subscription — delete and recreate it with sessions enabled if needed.

---

## Simulation Methods

Each function that publishes to Service Bus includes simulation methods for demo and testing purposes:

| Method | Location | Effect |
|---|---|---|
| `SimulateServiceBusFailure(bool)` | `ScheduledOutboxMessageProcessor` | Forces `ServiceBusException` inside the circuit breaker wrapper to trigger the open state |
| `SimulateTopicFailure(bool)` | `ScheduledUnpublishedTopicMessagesProcessor`, `ScheduledOrderStatusProcessor` | Same — triggers circuit breaker on topic publishes |
| `SimulateTopicFailure(bool)` | `OrderExecutionProvider` | Forces topic publish failure, populates `UnpublishedTopicMessages` |
| `RedirectIncomingMessagesToDeadLetterQueue(bool)` | `OrderExecutionProvider` | Throws on message receipt, causing Service Bus to retry and eventually DLQ the message after `MaxDeliveryCount` |

All simulation counters use `static int` fields so they persist across Azure Functions class re-instantiation between timer invocations.

---

## Prerequisites

- .NET 8 SDK
- Azure Functions Core Tools v4
- SQL Server (local or remote)
- Azure subscription with:
  - Service Bus namespace (Standard tier minimum — required for topics)
  - Key Vault
  - Application Insights
- Microsoft Teams workspace (for webhook notifications)

---

## Local Setup

### 1. Database

Run `Database/TradingApp_Setup.sql` against a local SQL Server instance. This creates the `TradingApp` database with all 7 tables, indexes, and constraints.

### 2. Key Vault Secrets

| Secret name | Value |
|---|---|
| `SqlConnectionString` | SQL Server connection string |
| `ServiceBusConnectionString` | Azure Service Bus primary connection string |
| `StorageConnectionString` | Azure Storage connection string (required by Functions runtime) |
| `APPLICATIONINSIGHTS-CONNECTION-STRING` | Application Insights connection string (note: hyphens, not underscores) |

### 3. local.settings.json (each Function project)

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/StorageConnectionString/)",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "SqlConnectionString": "@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/SqlConnectionString/)",
    "ServiceBusConnection": "@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/ServiceBusConnectionString/)",
    "ServiceBusConnectionString": "@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/ServiceBusConnectionString/)",
    "APPLICATIONINSIGHTS_CONNECTION_STRING": "@Microsoft.KeyVault(SecretUri=https://<vault>.vault.azure.net/secrets/APPLICATIONINSIGHTS-CONNECTION-STRING/)"
  }
}
```

`NotificationsProcessor/local.settings.json` additionally requires:

```json
"TeamsWebhookUrl": "https://yourcompany.webhook.office.com/webhookb2/..."
```

### 4. API appsettings.Development.json

```json
{
  "APPLICATIONINSIGHTS_CONNECTION_STRING": "<your-connection-string>"
}
```

### 5. Run

Start each project individually in separate terminals using `func start` per Function project and `dotnet run` for the API, or use the launch profiles in Visual Studio.

---

## Notes

- Key Vault and Service Bus are shared resources — running two instances simultaneously causes queue contention across separate local databases
- `DefaultAzureCredential` tries Visual Studio credentials first; `az login` is a fallback if VS auth fails
- Application Insights uses `EnableAdaptiveSampling = false` in the API to ensure all telemetry is captured during development
- Function `host.json` files set `"default": "Warning"` with function namespaces at `"Information"` to ensure key business events reach Application Insights without being dropped by adaptive sampling

---

## Project Structure

```
TradingApp/
├── Database/
│   └── TradingApp_Setup.sql                         # Full schema: 7 tables, indexes, constraints
├── Functions/
│   ├── OrderExecutionProvider/                      # Queue consumer + topic publisher
│   ├── ScheduledOutboxMessageProcessor/             # 3-phase outbox dispatcher with circuit breaker
│   ├── ScheduledUnpublishedTopicMessagesProcessor/  # Topic publish retry processor with circuit breaker
│   ├── ScheduledOrderStatusProcessor/              # ACK → FILLED promotion + FILLED event publisher
│   ├── DeadLetterQueueProcessor/                   # DLQ consumer → DeadLetterLogs
│   ├── NotificationsProcessor/                     # Sequence-ordered subscriber + Teams webhook
│   ├── RiskAnalysisProcessor/                      # Topic subscriber
│   └── AuditLogProcessor/                          # Topic subscriber
├── TradingApp.API/                                 # ASP.NET Core REST API
├── TradingApp.Business/                            # Services, repositories, DTOs, mappers
├── TradingApp.Domain/                              # Entities, enums, DbContext
├── TradingApp.Events/                              # Shared event/payload contracts
└── UI/
    └── TradingAppUI.html                           # Single-file testing dashboard
```

---

## Enum Reference

```
OrderStatus:        0 = PENDING_ACK  |  1 = ACKNOWLEDGED  |  2 = REJECTED  |  3 = FILLED
OutboxRetryReason:  0 = None  |  1 = ServiceBusUnavailable  |  2 = InvalidPayload  |  3 = DatabaseError  |  4 = Unknown
```
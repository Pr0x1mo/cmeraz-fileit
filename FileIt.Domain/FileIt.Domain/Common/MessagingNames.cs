namespace FileIt.Domain.Common;

/// <summary>
/// Shared contract defining Service Bus queue and topic names used across the FileIt system.
/// These names are referenced by both publishers (Services, SimpleFlow, DataFlow modules)
/// and subscribers (Functions listening on queues/topics).
/// </summary>
/// <remarks>
/// Placed in Domain because these are domain contracts with no external dependencies,
/// and both Infrastructure (publishers) and Application modules (subscribers) need them.
/// Centralizing avoids magic strings and provides a single source of truth.
/// </remarks>
public static class MessagingNames
{
    // ---- Queues ----

    /// <summary>
    /// Queue for API Add requests. Services module publishes, Services module subscribes.
    /// </summary>
    public const string ApiAddQueue = "api-add";

    /// <summary>
    /// Queue for DataFlow transform operations. DataFlow module publishes and subscribes.
    /// </summary>
    public const string DataFlowTransformQueue = "dataflow-transform";

    // ---- Topics ----

    /// <summary>
    /// Topic for API Add responses. Services module publishes, SimpleFlow/DataFlow subscribe.
    /// </summary>
    public const string ApiAddTopic = "api-add-topic";

    // ---- Subscriptions ----

    /// <summary>
    /// SimpleFlow module's subscription to the API Add topic.
    /// </summary>
    public const string ApiAddSimpleSubscription = "api-add-simple-sub";
}

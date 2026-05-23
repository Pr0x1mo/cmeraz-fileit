using Microsoft.Extensions.Logging;

namespace FileIt.Module.Ui.App;

/// <summary>
/// EventId catalog for this module. The numeric base (4000) is allocated
/// per-module so logs from different modules don't collide on EventId values.
/// SimpleFlow=2000, DataFlow=3000, Services=1000.
/// </summary>
public class UiEvents
{
    public static EventId UiWatcher = new EventId(4000, nameof(UiWatcher));
    public static EventId UiWatcherAddRequestLog = new EventId(
        9001,
        nameof(UiWatcherAddRequestLog)
    );
    public static EventId UiWatcherMoveToWorking = new EventId(
        9002,
        nameof(UiWatcherMoveToWorking)
    );
    public static EventId UiWatcherQueueApiAdd = new EventId(
        9003,
        nameof(UiWatcherQueueApiAdd)
    );

    public static EventId UiSubscriber = new EventId(9010, nameof(UiSubscriber));
    public static EventId UiSubscriberReceive = new EventId(
        9011,
        nameof(UiSubscriberReceive)
    );
    public static EventId UiSubscriberReceiveFailed = new EventId(
        9012,
        nameof(UiSubscriberReceiveFailed)
    );
    public static EventId UiSubscriberMessage = new EventId(
        9013,
        nameof(UiSubscriberMessage)
    );
    public static EventId UiSubscriberGetRequestLog = new EventId(
        9014,
        nameof(UiSubscriberGetRequestLog)
    );
    public static EventId UiSubscriberRequestLogNotFound = new EventId(
        9015,
        nameof(UiSubscriberRequestLogNotFound)
    );
    public static EventId UiSubscriberBlobNameMissing = new EventId(
        9016,
        nameof(UiSubscriberBlobNameMissing)
    );
    public static EventId UiSubscriberMoveToFinal = new EventId(
        9017,
        nameof(UiSubscriberMoveToFinal)
    );
    public static EventId UiSubscriberUpdateRequestLog = new EventId(
        9018,
        nameof(UiSubscriberUpdateRequestLog)
    );
    public static EventId UiSubscriberCompleted = new EventId(
        9019,
        nameof(UiSubscriberCompleted)
    );

    public static EventId UiTest = new EventId(9030, nameof(UiTest));
}

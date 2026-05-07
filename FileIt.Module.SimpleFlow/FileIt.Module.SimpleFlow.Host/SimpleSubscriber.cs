using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FileIt.Domain.Common;
using FileIt.Domain.Entities.Api;
using FileIt.Module.SimpleFlow.App;
using FileIt.Module.SimpleFlow.App.WaitOnApiUpload;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.SimpleFlow.Host;

public class SimpleSubscriber
{
    private readonly SimpleConfig _config;
    private readonly ILogger<SimpleSubscriber> _logger;
    private readonly IBasicApiAddHandler _responseHandler;

    public SimpleSubscriber(
        ILogger<SimpleSubscriber> logger,
        SimpleConfig config,
        IBasicApiAddHandler responseHandler
    )
    {
        _config = config;
        _logger = logger;
        _responseHandler = responseHandler;
    }

    /// <summary>
    /// A ServiceBusTrigger that processes the file ingested
    /// </summary>
    /// <param name="message">the ServiceBusReceivedMessage</param>
    /// <param name="context">the FunctionContext providing the CancellationToken</param>
    /// <returns></returns>
    [Function(nameof(SimpleSubscriber))]
    public async Task Run(
        [ServiceBusTrigger(MessagingNames.ApiAddTopic, MessagingNames.ApiAddSimpleSubscription)] ServiceBusReceivedMessage message,
        FunctionContext context
    )
    {
        var cancellationToken = context.CancellationToken;
        string clientRequestId = message.CorrelationId ?? string.Empty;

        using (
            _logger!.BeginScope(
                new Dictionary<string, object>()
                {
                    { "CorrelationId", clientRequestId ?? string.Empty },
                }
            )
        )
        {
            //LogFunctionStart(nameof(SimpleSubscriber));
            _logger.LogDebug(
                SimpleEvents.SimpleSubscriberReceive,
                "Receiving {@message}",
                message
            );

            var response = JsonSerializer.Deserialize<ApiAddResponse>(message.Body.ToString());
            if (response == null)
            {
                _logger.LogWarning(
                    SimpleEvents.SimpleSubscriberReceiveFailed,
                    "Failed to deserialize ApiAddResponse"
                );
                throw new ApplicationException("Failed to deserialize ApiAddResponse!");
            }

            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(SimpleEvents.SimpleSubscriber, "Processing ApiAddResponse");
            await _responseHandler.RunAsync(response, cancellationToken);
            //LogFunctionEnd(nameof(SimpleSubscriber));
        }
    }
}

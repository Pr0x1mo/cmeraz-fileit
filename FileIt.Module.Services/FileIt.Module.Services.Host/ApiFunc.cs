using System.Text.Json;
using Azure.Messaging.ServiceBus;
using FileIt.Domain.Common;
using FileIt.Module.Services.App;
using FileIt.Module.Services.App.ApiAdd;
using FileIt.Domain.Entities.Api;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FileIt.Module.Services.Host;

public class ApiFunc
{
    private readonly IApiAddCommand _command;
    private readonly ServicesConfig _config;
    private readonly ILogger<ApiFunc> _logger;

    public ApiFunc(IApiAddCommand command, ILogger<ApiFunc> logger, ServicesConfig config)
    {
        _command = command;
        _logger = logger;
        _config = config;
    }

    [Function(nameof(ApiAdd))]
    public async Task ApiAdd(
        [ServiceBusTrigger(MessagingNames.ApiAddQueue, Connection = "ServiceBus")]
            ServiceBusReceivedMessage message,
        FunctionContext context
    )
    {
        var cancellationToken = context.CancellationToken;

        using (
            _logger!.BeginScope(
                new Dictionary<string, object>()
                {
                    { "CorrelationId", message.CorrelationId ?? string.Empty },
                }
            )
        )
        {
            _logger.LogInformation(ServicesEvents.AddEvent, "ApiAdd started");
            ApiAddPayload? payload = null;
            string? bodystr = message.Body?.ToString();
            if (!string.IsNullOrWhiteSpace(bodystr))
            {
                payload = JsonSerializer.Deserialize<ApiAddPayload>(bodystr);
                _logger.LogDebug(
                    ServicesEvents.GetPayload,
                    "ApiAdd payload:\n{@ApiPayload}",
                    payload
                );
            }

            cancellationToken.ThrowIfCancellationRequested();

            var request = new ApiRequest(message.MessageId)
            {
                Body = payload,
                CorrelationId = message.CorrelationId,
                QueueName = _config.ApiAddQueueName,
                ReplyTo = _config.ApiAddTopicName,
                Subject = message.Subject,
            };
            _logger.LogDebug(ServicesEvents.ExecApiAdd, "ApiAdd request:\n{@ApiRequest}", request);
            await _command.ApiAdd(request, cancellationToken);
        }
    }
}

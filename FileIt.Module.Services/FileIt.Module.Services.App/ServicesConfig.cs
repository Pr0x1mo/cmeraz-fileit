using FileIt.Domain.Common;
using FileIt.Domain.Entities;
using FileIt.Domain.Interfaces;

namespace FileIt.Module.Services.App;

public class ServicesConfig
{
    public string? ApiAddTopicName { get; set; } = MessagingNames.ApiAddTopic;
    public string? ApiAddQueueName { get; set; } = MessagingNames.ApiAddQueue;
}

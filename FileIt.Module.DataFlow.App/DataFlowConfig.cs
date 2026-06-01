// This is our config class for the DataFlow module.
// It holds the names of the blob containers and service bus queue we use in this flow.
// These values get injected from local.settings.json when running locally,
// or from Azure App Settings when running in the cloud.
namespace FileIt.Module.DataFlow.App;

public class DataFlowConfig
{
    // The blob container where incoming GL Account CSV files get dropped
    public string SourceContainer { get; set; } = string.Empty;

    // The blob container where we move the file while we're processing it
    public string WorkingContainer { get; set; } = string.Empty;

    // The blob container where the transformed/exported output file lands when we're done
    public string FinalContainer { get; set; } = string.Empty;

    // The name of the service bus queue we use to signal that a file is ready to transform
    public string TransformQueueName { get; set; } = string.Empty;

    // The name of the service bus topic we publish to after the transform is complete
    public string TransformTopicName { get; set; } = string.Empty;
    // The numeric id of the Databricks Salesforce Workflow job that FileIt
    // triggers when a BIC_ActiveCustomer file lands. Comes from app settings.
    // (Created by Terraform; find it in the Databricks Workflows UI.)
    public long SalesforceDatabricksJobId { get; set; }
}

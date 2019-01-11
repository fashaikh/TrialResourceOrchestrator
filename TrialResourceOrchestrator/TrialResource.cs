using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;

namespace TrialResourceOrchestrator
{
    public enum ResourceStatus
    {
        Defined=0,
        DeploymentInProgress=1,
        AvailableForAssignment=2 ,
        Assigned=3,
        Deleted=4,
        Error=5
    }
    public class TrialResource
    {
        private int _deploymentCheckIntervalSeconds = 10;
        public Guid? ActivityId { get; set; }
        public string InstanceId { get; set; }
        public Guid? SessionId { get; set; }
        public Guid? CorrelationId { get; set; }
        public string TemplateName { get; set; }
        public string AppService{ get; set; }
        public string AppServiceName { get; set; }
        public string TenantId { get; set; }
        public string SubscriptionId { get; set; }
        public string ResourceGroupName { get; set; }
        public string Region { get; set; }
        public int DeploymentCheckTries { get; set; }
        public int DeploymentCheckAttempt { get; set; }
        public int DeploymentCheckIntervalSeconds { get { return Math.Max(10, _deploymentCheckIntervalSeconds); } set { _deploymentCheckIntervalSeconds = value; } }
        public string AzureEnvironment { get; set; }
        public string DeploymentLoggingLevel { get; set; }
        public string ARMTemplateLink { get; set; }
        public string ARMParameters { get; set; }
        public string DeploymentMode { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public ResourceStatus Status { get; set; }
        public string queuename
        {
            get { return TemplateName.Replace(" ", "-").ToLowerInvariant(); }

        }
    }
}

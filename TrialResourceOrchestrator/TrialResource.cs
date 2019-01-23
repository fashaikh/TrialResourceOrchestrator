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
        public Guid? ActivityId { get; set; }
        public string InstanceId { get; set; }
        public Guid? SessionId { get; set; }
        public Guid? CorrelationId { get; set; }
        public BaseTemplate Template { get; set; }
        public string AppService{ get; set; }
        public string AppServiceName { get; set; }
        public string TenantId { get; set; }
        public string SubscriptionId { get; set; }
        public string ResourceGroupName { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public ResourceStatus Status { get; set; }

    }
}

using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace TrialResourceOrchestrator
{
    public enum AppService
    {
        Web = 0,
        Api = 1,
        Logic = 2,
        Function = 3,
        Containers = 4,
        MonitoringTools = 5,
        Linux = 6,
        VSCodeLinux = 7

    }
    public class Config
    {
        [JsonProperty(PropertyName = "appService")]
        public string AppService { get; set; }
        [JsonProperty(PropertyName = "subscriptions")]
        public List<string> Subscriptions { get; set; }
        public string TenantName { get { return "72f988bf-86f1-41af-91ab-2d7cd011db47";  } }
    }
        public class BaseTemplate
    {
        private int _minQueueSize = 3;

        [JsonProperty(PropertyName = "dockerContainer")]
        public string DockerContainer { get; set; }

        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; }

        [JsonProperty(PropertyName = "sprite")]
        public string SpriteName { get; set; }

        [JsonProperty(PropertyName = "appService")]
        [JsonConverter(typeof(StringEnumConverter))]
        public AppService AppService { get; set; }

        [JsonProperty(PropertyName = "githubRepo")]
        public string GithubRepo { get; set; }
        [JsonProperty(PropertyName = "fileName")]
        public string FileName { get; set; }

        [JsonProperty(PropertyName = "language")]
        public string Language { get; set; }

        public string CsmTemplateFilePath { get; set; }

        public string CreateQueryString()
        {
            return string.Concat("appServiceName=", AppService.ToString(), "&name=", Name, "&autoCreate=true");
        }

        [JsonProperty(PropertyName = "msdeployPackageUrl")]
        public string MSDeployPackageUrl { get; set; }
        [JsonProperty(PropertyName = "isLinux")]
        public bool IsLinux { get { return Name.EndsWith("Web App on Linux"); } }

        [JsonProperty(PropertyName = "queueSizeToMaintain")]
        public int QueueSizeToMaintain { get { return Math.Max(3, _minQueueSize); } set { _minQueueSize = value; } }
        public int CurrentQueueSize { get; set; }
        public int ItemstoCreate { get { return QueueSizeToMaintain - CurrentQueueSize; } }

        [JsonProperty(PropertyName = "queuename")]
        public string QueueName { get { return Name.ToString().Trim().Replace(" ", "-").ToLowerInvariant(); } }


        [JsonProperty(PropertyName = "subscriptions")]
        public List<string> Subscriptions { get; set; }

    }

}

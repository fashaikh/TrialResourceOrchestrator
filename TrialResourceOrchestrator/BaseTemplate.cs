﻿using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.ResourceManager.Fluent;

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
        public string TenantId { get { return "72f988bf-86f1-41af-91ab-2d7cd011db47";  } }
        [JsonProperty(PropertyName = "regions")]
        public List<string> Regions { get; set; }

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
        public int QueueSizeToMaintain { get { return _minQueueSize; } set { _minQueueSize = Math.Min(value, _minQueueSize); } }
        public int CurrentQueueSize { get; set; }
        public int ItemstoCreate { get { return QueueSizeToMaintain - Math.Max(CurrentQueueSize, ResourceGroupsCreated); } }
        public int ResourceGroupsCreated { get; set; }

        [JsonProperty(PropertyName = "queuename")]
        public string QueueName { get { return Name.ToString().Trim().Replace(" ", "-").Replace(".", "-").ToLowerInvariant(); } }


        [JsonProperty(PropertyName = "subscriptions")]
        public List<string> Subscriptions { get; set; }

        [JsonProperty(PropertyName = "tenantId")]
        public string TenantId { get; set; }

        [JsonProperty(PropertyName = "region")]
        public string Region { get; set; }
        private int _deploymentCheckIntervalSeconds = 10;
        public int DeploymentCheckTries { get; set; }
        public int DeploymentCheckAttempt { get; set; }
        public int DeploymentCheckIntervalSeconds { get { return Math.Max(10, _deploymentCheckIntervalSeconds); } set { _deploymentCheckIntervalSeconds = value; } }
        public string AzureEnvironment { get { return Microsoft.Azure.Management.ResourceManager.Fluent.AzureEnvironment.AzureGlobalCloud.Name;  } }
        public string DeploymentLoggingLevel { get { return HttpLoggingDelegatingHandler.Level.BodyAndHeaders.ToString(); }  }
        public string ARMTemplateLink { get {return  "https://tro.blob.core.windows.net/armtemplates/" + CsmTemplateFilePath + ".json"; } }
        public string ARMParameters { get; set; }
        public string DeploymentMode { get { return Microsoft.Azure.Management.ResourceManager.Fluent.Models.DeploymentMode.Complete.ToString(); } }
    }

}

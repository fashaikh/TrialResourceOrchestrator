using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Rest;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using ExecutionContext = Microsoft.Azure.WebJobs.ExecutionContext;
using static Microsoft.Azure.Management.Fluent.Azure;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.Rest.Azure;

namespace TrialResourceOrchestrator.FunctionApp
{

    public static class DurableManageResources
    {

        //private static string key = TelemetryConfiguration.Active.InstrumentationKey = System.Environment.GetEnvironmentVariable("APPINSIGHTS_INSTRUMENTATIONKEY", EnvironmentVariableTarget.Process);
        //private static TelemetryClient telemetry = new TelemetryClient() { InstrumentationKey = key }; 


        [FunctionName("ManageResources")]
        public static async Task ManageResources([OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {
            context.SetCustomStatus("StartingManageResources");
            var templatesWithMetadata = await context.CallActivityAsync<List<BaseTemplate>>("GetTemplatesToManage", null);

            foreach (var template in templatesWithMetadata.Where(a=> a.Subscriptions.Count>0 && a.QueueSizeToMaintain>0))
            {
                var count = 0;//queueClient().GetQueueReference(template.QueueName).ApproximateMessageCount ?? 
                var resourcegroupsCreated = await context.CallActivityAsync<int>("GetResourceGroupsCreated", template);
                log.LogInformation($"Template: {template.Name} has queueSize: {count} and resourcegroupscreated: {resourcegroupsCreated}");
                template.CurrentQueueSize = count;
                template.ResourceGroupsCreated = resourcegroupsCreated;
            }
            context.SetCustomStatus("SetItemsToCreate");
            var taskList = new List<Task>();
            taskList.AddRange(templatesWithMetadata.Where(a => a.ItemstoCreate > 0 && a.QueueSizeToMaintain > 0).Select(a => context.CallSubOrchestratorAsync("CreateATrialResource", new TrialResource { Template = a })));
            context.SetCustomStatus("WaitForItemsToGetCreated");
            await Task.WhenAll(taskList.ToArray());
            context.SetCustomStatus("MissingItemsCreated");
            DateTime nextCleanup = context.CurrentUtcDateTime.AddSeconds(10);
            await context.CreateTimer(nextCleanup, CancellationToken.None);

            context.ContinueAsNew(null);

        }
        [FunctionName("CreateATrialResource")]
        public static async Task CreateATrialResource([OrchestrationTrigger] DurableOrchestrationContext context )
        {
            DateTime start = DateTime.UtcNow;
            TrialResource resource = context.GetInput<TrialResource>();
            context.SetCustomStatus("Started");
            resource.SessionId = Guid.NewGuid();
            resource.InstanceId = context.InstanceId;
            //telemetry.Context.Operation.Id = context.InstanceId;
            //telemetry.Context.Operation.Name = "CreateATrialResource";
            //if (!String.IsNullOrEmpty(resource.AppServiceName))
            //{
            //    telemetry.Context.User.Id = resource.AppServiceName;
            //}
            //telemetry.TrackEvent("DurableManageResources called");
            //telemetry.TrackMetric("Test Metric", DateTime.Now.Millisecond);

            //var outputs = new List<string>();

            resource = GetSubscriptionAndResourceGroupName(resource); 
            context.SetCustomStatus("ResourceConfigSet");

            resource = await context.CallActivityAsync<TrialResource>("CreateResourceGroup", resource);
            context.SetCustomStatus("ResourceGroupCreated");

            resource = await context.CallActivityAsync<TrialResource>("StartDeployment", resource);
            context.SetCustomStatus("DeploymentStarted");
            resource.Template.DeploymentCheckAttempt = 0;
            while ((resource.Status != ResourceStatus.AvailableForAssignment && resource.Status != ResourceStatus.Error) &&  resource.Template.DeploymentCheckAttempt++ < resource.Template.DeploymentCheckTries)
            {
                SdkContext.DelayProvider.Delay(resource.Template.DeploymentCheckIntervalSeconds * 1000);
                resource = await context.CallActivityAsync<TrialResource>("CheckDeploymentStatus", resource);
                context.SetCustomStatus("DeploymentGoingOn");
            }
            context.SetCustomStatus("DeploymentCompleted");

            resource = await context.CallActivityAsync<TrialResource>("AddResourceGroupToQueue", resource);
            context.SetCustomStatus("ResourceAddedToQueue");
            resource = await context.CallActivityAsync<TrialResource>("TagQueuedUpItem", resource);
            context.SetCustomStatus("QueuedItemTagged");

            //telemetry.TrackDependency( "ARM", "CreateATrialResource", JsonConvert.SerializeObject(resource), start, DateTime.UtcNow-start, true);
            // sleep between cleanups
            //DateTime nextCleanup = context.CurrentUtcDateTime.AddSeconds(10);
            //await context.CreateTimer(nextCleanup, CancellationToken.None);

            //context.ContinueAsNew(new TrialResource { TemplateName = "WordPress" });
            return;

        }

        [FunctionName("AddResourceGroupToQueue")]
        public static async Task AddResourceGroupToQueue([ActivityTrigger] TrialResource resource, ILogger log)
        {
            try
            {
                log.LogInformation($"Adding ResourceGroup {resource.ResourceGroupName} to queue {resource.Template.QueueName}  in {resource.SubscriptionId} region {resource.Template.Region}");

                var queue = queueClient().GetQueueReference(resource.Template.QueueName);
                await queue.CreateIfNotExistsAsync();
                await queue.AddMessageAsync(new CloudQueueMessage(JsonConvert.SerializeObject(resource)));

            }
            catch (Exception ex)
            {
                LogException("Exception queueing up ResourceGroup", ex, log);
            }
        }

        [FunctionName("TagQueuedUpItem")]
        public static async Task<TrialResource> TagQueuedUpItem([ActivityTrigger] TrialResource resource, ILogger log)
        {
            try
            {
                log.LogInformation($"Tagging queued Up Item {resource.ResourceGroupName}  in {resource.SubscriptionId} region {resource.Template.Region}");
                var armClient = await ArmClient.GetAzureClient(resource, log);
                var rg = await armClient.WithSubscription(resource.SubscriptionId)
                    .ResourceGroups.GetByNameAsync(resource.ResourceGroupName);
                await rg.Update().WithTag("deployed","true").ApplyAsync();
                resource.Status = ResourceStatus.AvailableForAssignment;
            }
            catch (Exception ex)
            {
                LogException("Exception tagging queued up Item", ex, log);
            }
            return resource;

        }

        [FunctionName("GetResourceGroupsCreated")]
        public static async Task<int> GetResourceGroupsCreated([ActivityTrigger] BaseTemplate template, ILogger log)
        {
            try
            {
                log.LogInformation($"Getting ResourceGroupsCreated in all subscriptions");
                var resource = new TrialResource { Template = template, TenantId= template.TenantId };
                var armClient = await ArmClient.GetAzureClient(resource, log);
                var count = 0;
                foreach (var sub in resource.Template.Subscriptions)
                {
                    var resourceGroups= await armClient
                    .WithSubscription(sub)
                    .ResourceGroups
                    .ListAsync();
                    count += resourceGroups.Where(a => GetQueueNameFromResourceGroupName(a.Name).Equals(template.QueueName)).Count();
                }
                return count;
            }
            catch (Exception ex)
            {
                LogException("Exception getting ResourceGroupsCreated", ex, log);
            }
            return -1;
        }

        private static string GetQueueNameFromResourceGroupName(string s)
        {
            var split = s.Split('-');
            return string.Join('-', split.Skip(1).SkipLast(1));
        }

        [FunctionName("CreateResourceGroup")]
        public static async Task<TrialResource> CreateResourceGroup([ActivityTrigger] TrialResource resource, ILogger log)
        {
            try
            {
                log.LogInformation($"Creating ResourceGroup {resource.ResourceGroupName} in {resource.SubscriptionId} region {resource.Template.Region}");

                var armClient = await ArmClient.GetAzureClient(resource,log);
                    await armClient
                    .WithSubscription(resource.SubscriptionId)
                    .ResourceGroups.Define(resource.ResourceGroupName)
                    .WithRegion(Region.Create(resource.Template.Region))
                    .CreateAsync();

            }
            catch (Exception ex) {
                LogException("Exception creating ResourceGroup", ex, log);
            }
            return resource;

        }
        [FunctionName("StartDeployment")]
        public static async Task<TrialResource> StartDeployment([ActivityTrigger] TrialResource resource, ILogger log)
        {
            try
            {
                log.LogInformation($"Starting deployment {resource.ResourceGroupName} in {resource.SubscriptionId}");
                var azure = await ArmClient.GetAzureClient(resource, log);
                await azure
                        .WithSubscription(resource.SubscriptionId)
                        .Deployments.Define(resource.AppServiceName)
                        .WithExistingResourceGroup(resource.ResourceGroupName)
                        .WithTemplateLink(resource.Template.ARMTemplateLink, null)
                        .WithParameters(JsonConvert.DeserializeObject(resource.Template.ARMParameters))
                        .WithMode(resource.Template.DeploymentMode.ToEnum<DeploymentMode>())
                        .CreateAsync();

            }
            catch (Exception ex)
            {
                LogException("Exception starting deployment", ex, log);
            }
            return resource;
        }

        private static void LogException(string prefix, Exception ex, ILogger log)
        {
            log.LogError($"{prefix} : {ex.Message } : {ex.StackTrace} : {(ex.InnerException == null ? ex.InnerException.Message : String.Empty)}: {((ex is CloudException) ? ((CloudException)ex).Body.Code + ((CloudException)ex).Body.Message + ((CloudException)ex).Body.Details + ((CloudException)ex).Body.AdditionalInfo : String.Empty)}");

        }
        [FunctionName("CheckDeploymentStatus")]
        public static async Task<TrialResource> CheckDeploymentStatus([ActivityTrigger] TrialResource resource, ILogger log)
        {
            try
            {   
                log.LogInformation($"Checking Deployment Status {resource.ResourceGroupName} in {resource.SubscriptionId} |Attempt:{resource.Template.DeploymentCheckAttempt}/{resource.Template.DeploymentCheckTries} |Template{resource.Template.Name} |Region:{resource.Template.Region}");
                var azure = await ArmClient.GetAzureClient(resource,log);

                var deployment = await azure.WithSubscription(resource.SubscriptionId).Deployments.GetByResourceGroupAsync(resource.ResourceGroupName, resource.AppServiceName);
                if (StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Succeeded"))
                {
                    resource.Status = ResourceStatus.AvailableForAssignment;
                }
                else if (StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Failed") || StringComparer.OrdinalIgnoreCase.Equals(deployment.ProvisioningState, "Cancelled"))
                {
                    resource.Status = ResourceStatus.Error;
                }
                else
                {
                    resource.Status = ResourceStatus.DeploymentInProgress;
                }
            }
            catch (Exception ex)
            {
                LogException("Exception checking DeploymentStatus", ex, log);
            }
            return resource;
        }
        [FunctionName("GetTemplatesToManage")]
        public static async Task<List<BaseTemplate>> GetTemplatesToManage([ActivityTrigger]string resource, ExecutionContext executionContext)
        {
            List<BaseTemplate> templates = JsonConvert.DeserializeObject<List<BaseTemplate>>(await File.ReadAllTextAsync(System.IO.Path.Combine(executionContext.FunctionAppDirectory, "config", "templates.json")));
            List<Config> config = JsonConvert.DeserializeObject<List<Config>>(await File.ReadAllTextAsync(System.IO.Path.Combine(executionContext.FunctionAppDirectory, "config", "config.json")));
            foreach (var template in templates)
            {
                var configToUse = config.First(a => a.AppService == template.AppService.ToString());
                template.Subscriptions = configToUse.Subscriptions;
                template.TenantId = configToUse.TenantId;
                template.Region = configToUse.Regions.Count > 0 ? configToUse.Regions.OrderBy(a => Guid.NewGuid()).FirstOrDefault() : String.Empty;
            }
            return templates.ToList();
        }
        // Retrieve storage account from connection string.
        private static CloudStorageAccount storageAccount = CloudStorageAccount.Parse(Environment.GetEnvironmentVariable("AzureWebJobsStorage"));
        private static CloudQueueClient queueClient()
        {
            // Create the blob client.
            return storageAccount.CreateCloudQueueClient();
        }

        public static TrialResource GetSubscriptionAndResourceGroupName([ActivityTrigger]TrialResource resource)
        {
            resource.AppService = resource.Template.AppService.ToString();
            resource.SubscriptionId = resource.Template.Subscriptions.OrderBy(a=>Guid.NewGuid()).FirstOrDefault();
            resource.TenantId = resource.Template.TenantId;
            var guid = Guid.NewGuid().ToString().Split('-')[1];
            resource.ResourceGroupName = $"try-{resource.Template.QueueName}-{guid}";
            resource.AppServiceName = resource.ResourceGroupName;
            resource.Template.ARMParameters = JsonConvert.SerializeObject( new Dictionary<string, Dictionary<string, object>>
            {
                { "msdeployPackageUrl", new Dictionary<string, object>{{"value", resource.Template.MSDeployPackageUrl??String.Empty} }},
                { "appServiceName", new Dictionary<string, object>{{"value", resource.AppServiceName } }}
            });
            return resource;
        }

        [FunctionName("DurableManageResources_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("CreateATrialResource", new TrialResource {});

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

  
        [FunctionName("GlobalInstance")]
        public static async Task Monitor([TimerTrigger("*/30 * * * * *", RunOnStartup = true)] TimerInfo info, [OrchestrationClient] DurableOrchestrationClient starter, ILogger log)
        {
            const string instanceId = "GlobalInstance";

            //Check if an instance with the specified ID already exists.
            var existingInstance= await starter.GetStatusAsync(instanceId);
            log.LogInformation($"Orchestration_Loop with ID '{instanceId}' had status '{existingInstance.RuntimeStatus}' at {DateTime.UtcNow.ToString()}");

            //await starter.PurgeInstanceHistoryAsync(instanceId);
            //await starter.TerminateAsync(instanceId, "Old");
            if (existingInstance == null)
            {
                // An instance with the specified ID doesn't exist, create one.
                //await starter.StartNewAsync("Orchestration_Loop", instanceId, null);
                await starter.StartNewAsync("ManageResources", instanceId, new TrialResource { });

                log.LogInformation($"Started Orchestration_Loop with ID = '{instanceId}'.");
                await starter.WaitForCompletionOrCreateCheckStatusResponseAsync(null, instanceId);
                log.LogInformation($"Completed Orchestration_Loop with ID = '{instanceId}'.");
            }
            else if (existingInstance.RuntimeStatus != OrchestrationRuntimeStatus.Running && existingInstance.RuntimeStatus != OrchestrationRuntimeStatus.ContinuedAsNew && existingInstance.RuntimeStatus != OrchestrationRuntimeStatus.Pending)
            {
                // An instance with the specified ID is at least scheduled, don't create a new one.
                await starter.TerminateAsync(instanceId, $"Terminating because existing RuntimeStatus is {existingInstance.RuntimeStatus}");
                log.LogInformation($"Orchestration_Loop with ID '{instanceId}' will be restarted. Previous status {existingInstance.RuntimeStatus}");
                await starter.StartNewAsync("ManageResources", instanceId, null);

            }
            else
            {
                log.LogInformation($"Orchestration_Loop with ID '{instanceId}' already exists. Sleeping");
            }

            return;
        }

        [FunctionName("StopInstance")]
        public static async Task<HttpResponseMessage> StopInstance(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            ILogger log)
        {
            const string instanceId = "GlobalInstance";

            //Check if an instance with the specified ID already exists.
            var existingInstance = await starter.GetStatusAsync(instanceId);
            log.LogInformation($"Orchestration_Loop with ID '{instanceId}' had status '{existingInstance.RuntimeStatus}' at {DateTime.UtcNow.ToString()}");

            await starter.TerminateAsync(instanceId, "Manually killed");
            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }

}